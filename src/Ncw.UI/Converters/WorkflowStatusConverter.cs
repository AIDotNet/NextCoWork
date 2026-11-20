using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Core.Domain;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

/// <summary>将工作流整体状态或单节点状态转成本地化界面文案。</summary>
public sealed class WorkflowStatusConverter : IValueConverter
{
    public static WorkflowStatusConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        WorkflowRunStatus s => NcwTexts.Get(s switch
        {
            WorkflowRunStatus.Queued => NcwTextKind.WorkflowStatusPending,
            WorkflowRunStatus.Running => NcwTextKind.WorkflowStatusRunning,
            WorkflowRunStatus.WaitingApproval => NcwTextKind.WorkflowStatusWaitingApproval,
            WorkflowRunStatus.Completed => NcwTextKind.WorkflowStatusCompleted,
            WorkflowRunStatus.Failed => NcwTextKind.WorkflowStatusFailed,
            WorkflowRunStatus.Cancelled => NcwTextKind.WorkflowStatusCancelled,
            _ => NcwTextKind.WorkflowStatusUnknown,
        }),
        WorkflowNodeRunStatus s => NcwTexts.Get(s switch
        {
            WorkflowNodeRunStatus.Pending => NcwTextKind.WorkflowStatusPending,
            WorkflowNodeRunStatus.Running => NcwTextKind.WorkflowStatusRunning,
            WorkflowNodeRunStatus.WaitingApproval => NcwTextKind.WorkflowStatusWaitingApproval,
            WorkflowNodeRunStatus.Done => NcwTextKind.WorkflowStatusCompleted,
            WorkflowNodeRunStatus.Failed => NcwTextKind.WorkflowStatusFailed,
            WorkflowNodeRunStatus.Skipped => NcwTextKind.WorkflowStatusSkipped,
            WorkflowNodeRunStatus.Cancelled => NcwTextKind.WorkflowStatusCancelled,
            _ => NcwTextKind.WorkflowStatusUnknown,
        }),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
