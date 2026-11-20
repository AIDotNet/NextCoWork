using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 对话流内联工作流卡片 ViewModel(docs/design/12-workflows.md):
/// 继承 <see cref="RowViewModel"/> 直接作为 <see cref="WorkflowRow"/> 转录行渲染;
/// 优先展示 <see cref="ClientStores.Workflows"/> 的实时快照,快照尚未到达或离线历史时自动用工具入参的 <see cref="WorkflowDefinition"/> 立即渲染画布。
/// 内置展开/收起、布局方向切换、缩放/缩略图、节点点击详情与重试、停止/续跑。
/// </summary>
public sealed partial class WorkflowRunCardViewModel : RowViewModel
{
    private readonly ClientStores _stores;
    private readonly IWorkflowCommands? _commands;
    private WorkflowDefinition? _fallbackDefinition;
    private ToolRowStatus _fallbackStatus = ToolRowStatus.Running;
    private string _fallbackTitle = "Workflow";
    private string? _fallbackEnv;
    private string? _watchedRunId;

    public WorkflowRunCardViewModel(
        ClientStores stores,
        string deviceId,
        string? runId,
        IWorkflowCommands? commands = null,
        string? rowKey = null,
        WorkflowRow? initialRow = null)
        : base(rowKey ?? initialRow?.Key ?? $"wf:{deviceId}:{runId ?? Guid.NewGuid().ToString("N")}")
    {
        _stores = stores;
        DeviceId = deviceId;
        RunId = runId ?? initialRow?.RunId;
        _commands = commands;
        if (initialRow is not null)
        {
            ApplyRow(initialRow);
        }

        stores.Workflows.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        EnsureWatching();
        Refresh();
    }

    public string DeviceId { get; }

    public string? RunId { get; private set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty] public partial WorkflowLayoutDirection LayoutDirection { get; set; } = WorkflowLayoutDirection.LeftToRight;

    [ObservableProperty] public partial WorkflowDefinition? Definition { get; private set; }

    [ObservableProperty] public partial WorkflowRunRecord? Run { get; private set; }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    [ObservableProperty] public partial WorkflowRunStatus DerivedStatus { get; private set; } = WorkflowRunStatus.Unknown;

    [ObservableProperty] public partial string ProgressText { get; private set; } = "";

    [ObservableProperty] public partial string? EnvironmentBadge { get; private set; }

    [ObservableProperty] public partial bool CanStop { get; private set; }

    [ObservableProperty] public partial bool CanResume { get; private set; }

    [ObservableProperty] public partial bool IsUnknown { get; private set; }

    [ObservableProperty] public partial string? SelectedNodeId { get; set; }

    [ObservableProperty] public partial WorkflowNodeDef? SelectedNodeDef { get; private set; }

    [ObservableProperty] public partial WorkflowNodeRunRecord? SelectedNodeRun { get; private set; }

    [ObservableProperty] public partial bool CanRetrySelectedNode { get; private set; }

    [ObservableProperty] public partial string? SelectedNodeDurationText { get; private set; }

    [ObservableProperty] public partial string? SelectedNodeInputText { get; private set; }

    [ObservableProperty] public partial string? SelectedNodeOutputText { get; private set; }

    [ObservableProperty] public partial bool IsSelectedNodeMarkdown { get; private set; }

    public bool HasSelectedNode => SelectedNodeDef is not null;

    public bool HasSelectedNodeInput => !string.IsNullOrWhiteSpace(SelectedNodeInputText);

    public bool HasSelectedNodeOutput => !string.IsNullOrWhiteSpace(SelectedNodeOutputText);

    public bool HasSelectedNodeError => !string.IsNullOrWhiteSpace(SelectedNodeRun?.Error);

    public bool IsSelectedNodeEmpty => !HasSelectedNodeOutput && !HasSelectedNodeError;

    partial void OnSelectedNodeIdChanged(string? value) => RefreshSelectedNode();

    public override bool Update(TranscriptRow row)
    {
        if (row is WorkflowRow wf)
        {
            ApplyRow(wf);
            EnsureWatching();
        }

        Refresh();
        return true;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleLayoutDirection() =>
        LayoutDirection = LayoutDirection == WorkflowLayoutDirection.LeftToRight
            ? WorkflowLayoutDirection.TopToBottom
            : WorkflowLayoutDirection.LeftToRight;

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_commands is not null && CanStop && RunId is { Length: > 0 } id)
        {
            await _commands.CancelAsync(DeviceId, id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (_commands is not null && CanResume && RunId is { Length: > 0 } id)
        {
            await _commands.ResumeAsync(DeviceId, id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RetrySelectedNodeAsync()
    {
        if (_commands is not null && SelectedNodeId is not null && CanRetrySelectedNode && RunId is { Length: > 0 } id)
        {
            await _commands.RetryNodeAsync(DeviceId, id, SelectedNodeId).ConfigureAwait(true);
        }
    }

    private void ApplyRow(WorkflowRow row)
    {
        if (!string.IsNullOrEmpty(row.RunId))
        {
            RunId = row.RunId;
        }

        if (row.FallbackDefinition is not null)
        {
            _fallbackDefinition = row.FallbackDefinition;
        }

        _fallbackStatus = row.Status;
        _fallbackTitle = row.Title;
        _fallbackEnv = row.Env?.Name;
    }

    private void EnsureWatching()
    {
        if (_commands is null || string.IsNullOrEmpty(RunId) || string.Equals(_watchedRunId, RunId, StringComparison.Ordinal))
        {
            return;
        }

        _watchedRunId = RunId;
        _ = _commands.WatchRunAsync(DeviceId, RunId);
    }

    private void Refresh()
    {
        var conn = _stores.Workspace.State.Devices.TryGetValue(DeviceId, out var dev)
            ? dev.Connection
            : DeviceConnection.Offline;

        if (RunId is { Length: > 0 } id && _stores.Workflows.State.Runs.TryGetValue(new WorkflowRunKey(DeviceId, id), out var run))
        {
            Run = run;
            Definition = run.Definition;
            Title = run.Title;
            EnvironmentBadge = run.EnvironmentId ?? _fallbackEnv;
            DerivedStatus = WorkflowView.DeriveStatus(run.Status, conn);
            IsUnknown = DerivedStatus == WorkflowRunStatus.Unknown;
            CanStop = WorkflowView.CanCancel(run.Status, conn);
            CanResume = WorkflowView.CanResume(run.Status, conn);
            var progress = WorkflowView.ProgressOf(run);
            ProgressText = $"{progress.Completed}/{progress.Total}";
            RefreshSelectedNode();
            return;
        }

        // 回退:快照尚未拉回(或历史会话离线)时,直接用工具入参的 DAG 渲染画布
        Definition = _fallbackDefinition;
        Title = _fallbackTitle;
        EnvironmentBadge = _fallbackEnv;
        DerivedStatus = _fallbackStatus switch
        {
            ToolRowStatus.Done => WorkflowRunStatus.Completed,
            ToolRowStatus.Failed => WorkflowRunStatus.Failed,
            ToolRowStatus.Running => WorkflowRunStatus.Running,
            _ => WorkflowRunStatus.Unknown,
        };
        IsUnknown = DerivedStatus == WorkflowRunStatus.Unknown;
        CanStop = false;
        CanResume = false;

        if (_fallbackDefinition is { } def)
        {
            var nodeStatus = _fallbackStatus switch
            {
                ToolRowStatus.Done => WorkflowNodeRunStatus.Done,
                ToolRowStatus.Failed => WorkflowNodeRunStatus.Failed,
                ToolRowStatus.Running => WorkflowNodeRunStatus.Running,
                _ => WorkflowNodeRunStatus.Pending,
            };
            var edgeStatus = _fallbackStatus == ToolRowStatus.Done ? WorkflowEdgeRunStatus.Active : WorkflowEdgeRunStatus.Idle;
            Run = new WorkflowRunRecord(
                RunId: RunId ?? Key,
                WorkspaceId: "",
                Title: _fallbackTitle,
                Status: DerivedStatus,
                Definition: def,
                Nodes: [.. def.Nodes.Select(n => new WorkflowNodeRunRecord(n.Id, n.Kind, nodeStatus))],
                Edges: [.. def.Edges.Select(e => new WorkflowEdgeRunRecord(e.Id, edgeStatus))]);
            ProgressText = _fallbackStatus == ToolRowStatus.Done ? $"{def.Nodes.Count}/{def.Nodes.Count}" : $"0/{def.Nodes.Count}";
        }
        else
        {
            Run = null;
            ProgressText = "";
        }

        RefreshSelectedNode();
    }

    private void RefreshSelectedNode()
    {
        SelectedNodeDef = SelectedNodeId is not null
            ? Definition?.Nodes.FirstOrDefault(n => string.Equals(n.Id, SelectedNodeId, StringComparison.Ordinal))
            : null;
        SelectedNodeRun = SelectedNodeId is not null
            ? Run?.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, SelectedNodeId, StringComparison.Ordinal))
            : null;

        var conn = _stores.Workspace.State.Devices.TryGetValue(DeviceId, out var dev)
            ? dev.Connection
            : DeviceConnection.Offline;
        CanRetrySelectedNode = Run is not null && SelectedNodeRun is not null && RunId is { Length: > 0 }
            && WorkflowView.CanRetryNode(Run.Status, SelectedNodeRun.Status, conn);

        SelectedNodeDurationText = SelectedNodeRun is { StartedAt: { } s, FinishedAt: { } f } && f >= s
            ? FormatDuration(f - s)
            : null;
        SelectedNodeInputText = FormatNodeInput(SelectedNodeDef);
        SelectedNodeOutputText = !string.IsNullOrWhiteSpace(SelectedNodeRun?.OutputText)
            ? SelectedNodeRun!.OutputText
            : SelectedNodeRun?.Summary;
        IsSelectedNodeMarkdown = SelectedNodeDef?.Kind is WorkflowNodeKind.Agent or WorkflowNodeKind.Output;

        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(HasSelectedNodeInput));
        OnPropertyChanged(nameof(HasSelectedNodeOutput));
        OnPropertyChanged(nameof(HasSelectedNodeError));
        OnPropertyChanged(nameof(IsSelectedNodeEmpty));
    }

    private static string FormatDuration(long ms) => ms < 1000
        ? $"{ms} ms"
        : ms < 60_000
            ? $"{ms / 1000.0:0.0} s"
            : $"{ms / 60_000}m {(ms % 60_000) / 1000}s";

    private static string? FormatNodeInput(WorkflowNodeDef? node)
    {
        if (node?.Config is not { } cfg)
        {
            return null;
        }

        return node.Kind switch
        {
            WorkflowNodeKind.Agent or WorkflowNodeKind.Approval => cfg.Prompt,
            WorkflowNodeKind.Tool => string.IsNullOrWhiteSpace(cfg.ToolArgsJson)
                ? cfg.ToolName
                : $"{cfg.ToolName}\n{cfg.ToolArgsJson}",
            WorkflowNodeKind.Condition => cfg.Expression,
            WorkflowNodeKind.Output => cfg.OutputTemplate,
            _ => null,
        };
    }
}
