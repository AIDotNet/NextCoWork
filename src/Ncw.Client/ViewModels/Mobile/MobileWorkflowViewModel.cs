using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels.Mobile;

/// <summary>移动端垂直步骤时间线的一行(按 DAG 拓扑序派生)。</summary>
public sealed record MobileWorkflowStepItem(
    string NodeId,
    string Title,
    WorkflowNodeKind Kind,
    WorkflowNodeRunStatus Status,
    string? Summary,
    string? Error,
    string? OutputPreview,
    string? DurationText);

/// <summary>
/// 移动端(<c>&lt; 720px</c>)工作流监控页 ViewModel(docs/design/12-workflows.md §9.3):
/// 顶部只读缩略画布 + 下方拓扑序垂直步骤时间线,不开放触控拖拽编辑。
/// </summary>
public sealed partial class MobileWorkflowViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IWorkflowCommands? _commands;

    public MobileWorkflowViewModel(ClientStores stores, string deviceId, string runId, IWorkflowCommands? commands = null)
    {
        _stores = stores;
        DeviceId = deviceId;
        RunId = runId;
        _commands = commands;
        stores.Workflows.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public string DeviceId { get; }

    public string RunId { get; }

    public ObservableCollection<MobileWorkflowStepItem> Steps { get; } = [];

    [ObservableProperty] public partial WorkflowDefinition? Definition { get; private set; }

    [ObservableProperty] public partial WorkflowRunRecord? Run { get; private set; }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    [ObservableProperty] public partial WorkflowRunStatus DerivedStatus { get; private set; } = WorkflowRunStatus.Unknown;

    [ObservableProperty] public partial string ProgressText { get; private set; } = "";

    [ObservableProperty] public partial bool CanStop { get; private set; }

    [ObservableProperty] public partial bool CanResume { get; private set; }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_commands is not null && CanStop)
        {
            await _commands.CancelAsync(DeviceId, RunId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (_commands is not null && CanResume)
        {
            await _commands.ResumeAsync(DeviceId, RunId).ConfigureAwait(true);
        }
    }

    private void Refresh()
    {
        var conn = _stores.Workspace.State.Devices.TryGetValue(DeviceId, out var dev)
            ? dev.Connection
            : DeviceConnection.Offline;

        if (!_stores.Workflows.State.Runs.TryGetValue(new WorkflowRunKey(DeviceId, RunId), out var run))
        {
            Run = null;
            Definition = null;
            DerivedStatus = WorkflowRunStatus.Unknown;
            CanStop = false;
            CanResume = false;
            Steps.Clear();
            return;
        }

        Run = run;
        Definition = run.Definition;
        Title = run.Title;
        DerivedStatus = WorkflowView.DeriveStatus(run.Status, conn);
        CanStop = WorkflowView.CanCancel(run.Status, conn);
        CanResume = WorkflowView.CanResume(run.Status, conn);
        var progress = WorkflowView.ProgressOf(run);
        ProgressText = $"{progress.Completed}/{progress.Total}";

        var nodeStates = run.Nodes.ToDictionary(static n => n.NodeId, StringComparer.Ordinal);
        var ordered = WorkflowValidator.TopologicalOrder(run.Definition);
        if (ordered.Count == 0)
        {
            ordered = run.Definition.Nodes;
        }

        Steps.Clear();
        foreach (var defNode in ordered)
        {
            var nr = nodeStates.GetValueOrDefault(defNode.Id);
            string? duration = nr?.StartedAt is { } s && nr.FinishedAt is { } f && f >= s
                ? $"{(f - s) / 1000.0:0.0}s"
                : null;
            var preview = nr?.OutputText is { Length: > 240 } txt ? txt[..240] + "…" : nr?.OutputText;
            Steps.Add(new MobileWorkflowStepItem(
                defNode.Id,
                defNode.Title,
                defNode.Kind,
                nr?.Status ?? WorkflowNodeRunStatus.Pending,
                nr?.Summary ?? defNode.Description,
                nr?.Error,
                preview,
                duration));
        }
    }
}
