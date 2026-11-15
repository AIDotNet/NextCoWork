using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

/// <summary>失败(<see cref="Problem"/>)→ 当前语言的一句话。按稳定的 <c>files.*</c> / <c>git.*</c> / <c>terminals.*</c> key 翻译,没有 key 就按结局类别。</summary>
public sealed class ProblemTextConverter : IValueConverter
{
    public static readonly ProblemTextConverter Instance = new();

    public static string Text(Problem problem) => NcwTexts.Get(problem.Key switch
    {
        FilesErrorKeys.NotFound or TerminalErrorKeys.NotFound => NcwTextKind.ProblemNotFound,
        FilesErrorKeys.Exists => NcwTextKind.ProblemExists,
        FilesErrorKeys.Conflict or TerminalErrorKeys.Conflict => NcwTextKind.ProblemConflict,
        FilesErrorKeys.InvalidPath or FilesErrorKeys.Symlink or GitErrorKeys.InvalidPath or TerminalErrorKeys.InvalidPath => NcwTextKind.ProblemInvalidPath,
        FilesErrorKeys.InvalidName => NcwTextKind.ProblemInvalidName,
        FilesErrorKeys.Permission => NcwTextKind.ProblemPermission,
        FilesErrorKeys.TooLarge or TerminalErrorKeys.TooLarge => NcwTextKind.ProblemTooLarge,
        FilesErrorKeys.Disconnected or FilesErrorKeys.ProjectUnavailable or TerminalErrorKeys.Disconnected or TerminalErrorKeys.ProjectUnavailable => NcwTextKind.ProblemUnavailable,
        GitErrorKeys.NothingStaged => NcwTextKind.ProblemNothingStaged,
        GitErrorKeys.EmptyMessage => NcwTextKind.ProblemEmptyMessage,
        GitErrorKeys.DetachedPush => NcwTextKind.ProblemDetachedPush,
        GitErrorKeys.InvalidBranch => NcwTextKind.ProblemInvalidBranch,
        GitErrorKeys.Timeout => NcwTextKind.ProblemGitTimeout,
        GitErrorKeys.AiNoModel => NcwTextKind.ProblemNoModel,
        GitErrorKeys.AiFailed or GitErrorKeys.AiTimeout or GitErrorKeys.AiTruncated or GitErrorKeys.AiUnparsable => NcwTextKind.ProblemAiFailed,
        GitErrorKeys.Failed => NcwTextKind.ProblemGitFailed,
        TerminalErrorKeys.LimitReached => NcwTextKind.ProblemTerminalLimit,
        TerminalErrorKeys.ApprovalExpired or TerminalErrorKeys.ApprovalRequired or TerminalErrorKeys.Cancelled => NcwTextKind.ProblemApprovalExpired,
        TerminalErrorKeys.Exited => NcwTextKind.ProblemTerminalExited,
        TerminalErrorKeys.Unsupported => NcwTextKind.ProblemTerminalUnsupported,
        _ => problem.Outcome switch
        {
            ActionOutcome.Unavailable => NcwTextKind.ProblemUnavailable,
            ActionOutcome.Forbidden => NcwTextKind.ProblemForbidden,
            ActionOutcome.NotFound => NcwTextKind.ProblemNotFound,
            ActionOutcome.Conflict => NcwTextKind.ProblemConflict,
            _ => NcwTextKind.ProblemFailed,
        },
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Problem problem ? Text(problem) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>失败里设备给的英文诊断(放在提示里)。</summary>
public sealed class ProblemDetailConverter : IValueConverter
{
    public static readonly ProblemDetailConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Problem { Detail: { Length: > 0 } detail } ? detail : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字节数 → 「12.3 KB」。</summary>
public sealed class FileSizeConverter : IValueConverter
{
    public static readonly FileSizeConverter Instance = new();

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0 / 1024.0:0.#} GB"),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? Format(bytes) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>图片字节 → 位图(文件预览)。解码失败返回 null。</summary>
public sealed class BytesToBitmapConverter : IValueConverter
{
    public static readonly BytesToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>二进制文件的原因(<see cref="FilePreviewViewModel.Reason"/>)→ 说明。</summary>
public sealed class BinaryReasonTextConverter : IValueConverter
{
    public static readonly BinaryReasonTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => NcwTexts.Get(value switch
    {
        "tooLarge" => NcwTextKind.FileTooLarge,
        "encoding" => NcwTextKind.FileNotUtf8,
        _ => NcwTextKind.FileBinary,
    });

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Git 不可用的原因(<see cref="GitUnavailableReasons"/>)→ 说明。</summary>
public sealed class GitUnavailableTextConverter : IValueConverter
{
    public static readonly GitUnavailableTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => NcwTexts.Get(value switch
    {
        GitUnavailableReasons.GitMissing => NcwTextKind.GitMissing,
        GitUnavailableReasons.WorkspaceUnavailable => NcwTextKind.GitWorkspaceUnavailable,
        _ => NcwTextKind.GitNoRepository,
    });

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>改动审查撤销 / 重做的结局 → 一句话。</summary>
public sealed class ReviewOutcomeTextConverter : IValueConverter
{
    public static readonly ReviewOutcomeTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is ReviewOutcome outcome
        ? outcome.IsAllOk
            ? NcwTexts.Format(NcwTextKind.ReviewDone, outcome.Ok)
            : NcwTexts.Format(NcwTextKind.ReviewPartial, outcome.Ok, outcome.Conflict, outcome.Skipped, outcome.Error)
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>终端怎么结束的 → 一句话。</summary>
public sealed class TerminalExitTextConverter : IValueConverter
{
    public static readonly TerminalExitTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TerminalExit exit
        ? exit.Kind switch
        {
            TerminalExitKind.Code => NcwTexts.Format(NcwTextKind.TerminalExitCode, exit.Value),
            TerminalExitKind.Signal => NcwTexts.Format(NcwTextKind.TerminalExitSignal, exit.Value),
            TerminalExitKind.Killed => NcwTexts.Get(NcwTextKind.TerminalExitKilled),
            TerminalExitKind.ConnectionLost => NcwTexts.Get(NcwTextKind.TerminalExitLost),
            _ => NcwTexts.Get(NcwTextKind.TerminalExitPlain),
        }
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>点开 / 停止子代理没成功的原因 → 明确的提示。</summary>
public sealed class SubagentNoticeTextConverter : IValueConverter
{
    public static readonly SubagentNoticeTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SubagentNotice.NoChildSession => NcwTexts.Get(NcwTextKind.SubagentNoChildSession),
        SubagentNotice.Unavailable => NcwTexts.Get(NcwTextKind.SubagentOpenUnavailable),
        SubagentNotice.CannotStop => NcwTexts.Get(NcwTextKind.SubagentCannotStop),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
