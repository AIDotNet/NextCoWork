using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ncw.Core.Domain;
using Ncw.UI.Localization;
using TKCode.Arc.Icons.Lucide;
using ArcWorkflowAutoFit = TKCode.Arc.WorkflowAutoFit;
using ArcWorkflowCanvas = TKCode.Arc.WorkflowCanvas;
using ArcWorkflowEdge = TKCode.Arc.WorkflowEdge;
using ArcWorkflowEdgeState = TKCode.Arc.WorkflowEdgeState;
using ArcWorkflowGraph = TKCode.Arc.WorkflowGraph;
using ArcWorkflowLayoutDirection = TKCode.Arc.WorkflowLayoutDirection;
using ArcWorkflowMinimap = TKCode.Arc.WorkflowMinimap;
using ArcWorkflowNode = TKCode.Arc.WorkflowNode;
using ArcWorkflowNodeAppearance = TKCode.Arc.WorkflowNodeAppearance;
using ArcWorkflowNodeState = TKCode.Arc.WorkflowNodeState;
using ArcWorkflowPort = TKCode.Arc.WorkflowPort;
using ArcWorkflowPortDirection = TKCode.Arc.WorkflowPortDirection;
using ArcWorkflowZoomControls = TKCode.Arc.WorkflowZoomControls;

namespace Ncw.UI.Controls;

/// <summary>
/// 对 <c>TKCode.Arc.Workflow</c> 的唯一薄封装(AGENTS.md §2,docs/design/12-workflows.md §9.1)。
/// 业务视图只引用本控件,不直接引用 <c>WorkflowCanvas</c> / <c>WorkflowMinimap</c> / <c>WorkflowZoomControls</c>。
/// 内部采用增量 Diff 投影(<see cref="WorkflowDefinition"/> + <see cref="WorkflowRunRecord"/> → <see cref="ArcWorkflowGraph"/>),
/// 复用节点对象以保留 <c>QueueRunVisuals</c> 的连线流动动效。
/// </summary>
public sealed class NcwWorkflowCanvas : UserControl
{
    public static readonly StyledProperty<WorkflowDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, WorkflowDefinition?>(nameof(Definition));

    public static readonly StyledProperty<WorkflowRunRecord?> RunProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, WorkflowRunRecord?>(nameof(Run));

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, bool>(nameof(IsEditable), defaultValue: false);

    public static readonly StyledProperty<bool> ShowChromeProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, bool>(nameof(ShowChrome), defaultValue: true);

    public static readonly StyledProperty<bool> ShowMinimapProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, bool>(nameof(ShowMinimap), defaultValue: false);

    public static readonly StyledProperty<bool> IsPanZoomEnabledProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, bool>(nameof(IsPanZoomEnabled), defaultValue: true);

    public static readonly StyledProperty<WorkflowLayoutDirection> LayoutDirectionProperty =
        AvaloniaProperty.Register<NcwWorkflowCanvas, WorkflowLayoutDirection>(nameof(LayoutDirection), defaultValue: WorkflowLayoutDirection.TopToBottom);

    private readonly ArcWorkflowCanvas _canvas;
    private readonly ArcWorkflowMinimap _minimap;
    private readonly ArcWorkflowZoomControls _zoom;
    private readonly StackPanel _chromeOverlay;
    private readonly ArcWorkflowGraph _graph = new() { AllowCycles = false };
    private string? _loadedDefinitionId;
    private bool _syncing;

    public NcwWorkflowCanvas()
    {
        _canvas = new ArcWorkflowCanvas
        {
            Graph = _graph,
            ShowGrid = true,
            SnapToGrid = true,
            QueueRunVisuals = true,
            AutoFit = ArcWorkflowAutoFit.ShrinkToFit,
        };

        _minimap = new ArcWorkflowMinimap
        {
            Target = _canvas,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _zoom = new ArcWorkflowZoomControls
        {
            Target = _canvas,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _chromeOverlay = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Children = { _minimap, _zoom },
        };

        var root = new Panel();
        root.Children.Add(_canvas);
        root.Children.Add(_chromeOverlay);
        Content = root;

        _canvas.SelectionChanged += (_, _) =>
        {
            var selected = _canvas.SelectedNodes.FirstOrDefault()?.Id;
            NodeSelected?.Invoke(selected);
        };

        _canvas.NodeActivated += (_, e) => NodeActivated?.Invoke(e.Node.Id);

        _canvas.GraphEdited += (_, _) =>
        {
            if (_syncing || Definition is null)
            {
                return;
            }

            DefinitionEdited?.Invoke(ExtractDefinition(Definition));
        };

        _canvas.ConnectionDroppedOnCanvas += (_, e) =>
        {
            if (_syncing || !IsEditable)
            {
                return;
            }

            ConnectionDropped?.Invoke(e.SourceNode.Id, e.SourcePort?.Id, e.Position.X, e.Position.Y);
        };
    }

    public WorkflowDefinition? Definition
    {
        get => GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    public WorkflowRunRecord? Run
    {
        get => GetValue(RunProperty);
        set => SetValue(RunProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public bool ShowChrome
    {
        get => GetValue(ShowChromeProperty);
        set => SetValue(ShowChromeProperty, value);
    }

    public bool ShowMinimap
    {
        get => GetValue(ShowMinimapProperty);
        set => SetValue(ShowMinimapProperty, value);
    }

    public bool IsPanZoomEnabled
    {
        get => GetValue(IsPanZoomEnabledProperty);
        set => SetValue(IsPanZoomEnabledProperty, value);
    }

    public WorkflowLayoutDirection LayoutDirection
    {
        get => GetValue(LayoutDirectionProperty);
        set => SetValue(LayoutDirectionProperty, value);
    }

    public event Action<string?>? NodeSelected;

    public event Action<string>? NodeActivated;

    public event Action<WorkflowDefinition>? DefinitionEdited;

    public event Action<string, string?, double, double>? ConnectionDropped;

    public void ArrangeNodes() => _canvas.ArrangeNodes(animate: true);

    public void FitToContent() => _canvas.FitToContent(animate: true);

    public void Undo() => _canvas.Undo();

    public void Redo() => _canvas.Redo();

    public void ClearSelection() => _canvas.Select([]);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsEditableProperty)
        {
            _canvas.IsEditable = IsEditable;
        }
        else if (change.Property == ShowChromeProperty)
        {
            _chromeOverlay.IsVisible = ShowChrome;
        }
        else if (change.Property == ShowMinimapProperty)
        {
            _minimap.IsVisible = ShowMinimap;
        }
        else if (change.Property == IsPanZoomEnabledProperty)
        {
            _canvas.IsPanZoomEnabled = IsPanZoomEnabled;
        }
        else if (change.Property == LayoutDirectionProperty)
        {
            _canvas.LayoutDirection = LayoutDirection == WorkflowLayoutDirection.LeftToRight
                ? ArcWorkflowLayoutDirection.LeftToRight
                : ArcWorkflowLayoutDirection.TopToBottom;
        }
        else if (change.Property == DefinitionProperty || change.Property == RunProperty)
        {
            SyncGraph();
        }
    }

    private void SyncGraph()
    {
        _syncing = true;
        try
        {
            var def = Definition;
            if (def is null)
            {
                _graph.Edges.Clear();
                _graph.Nodes.Clear();
                _loadedDefinitionId = null;
                return;
            }

            var isNewGraph = !string.Equals(_loadedDefinitionId, def.Id, StringComparison.Ordinal);
            if (isNewGraph)
            {
                _graph.Edges.Clear();
                _graph.Nodes.Clear();
                _graph.History.Clear();
                _loadedDefinitionId = def.Id;
            }

            var run = Run;
            var runNodes = run?.Nodes.ToDictionary(static n => n.NodeId, StringComparer.Ordinal);
            var runEdges = run?.Edges.ToDictionary(static e => e.EdgeId, StringComparer.Ordinal);

            var existingNodes = _graph.Nodes.ToDictionary(static n => n.Id, StringComparer.Ordinal);
            var desiredNodeIds = new HashSet<string>(def.Nodes.Select(static n => n.Id), StringComparer.Ordinal);

            // 移除已删除节点
            for (var i = _graph.Nodes.Count - 1; i >= 0; i--)
            {
                if (!desiredNodeIds.Contains(_graph.Nodes[i].Id))
                {
                    _graph.Nodes.RemoveAt(i);
                }
            }

            // 增量新增 / 更新节点
            foreach (var defNode in def.Nodes)
            {
                if (!existingNodes.TryGetValue(defNode.Id, out var arcNode))
                {
                    arcNode = new ArcWorkflowNode(defNode.Id, defNode.Title)
                    {
                        HasDefaultInput = defNode.Kind != WorkflowNodeKind.Trigger,
                    };
                    if (defNode.Position is { } pos)
                    {
                        arcNode.Position = new Point(pos.X, pos.Y);
                    }

                    if (defNode.Inputs is { Count: > 0 })
                    {
                        foreach (var p in defNode.Inputs)
                        {
                            arcNode.Inputs.Add(new ArcWorkflowPort(ArcWorkflowPortDirection.Input, p.Id, p.Label)
                            {
                                DataType = p.DataType,
                                MaxConnections = p.MaxConnections ?? 0,
                            });
                        }
                    }

                    if (defNode.Outputs is { Count: > 0 })
                    {
                        foreach (var p in defNode.Outputs)
                        {
                            arcNode.Outputs.Add(new ArcWorkflowPort(ArcWorkflowPortDirection.Output, p.Id, p.Label)
                            {
                                DataType = p.DataType,
                                MaxConnections = p.MaxConnections ?? 0,
                            });
                        }
                    }

                    _graph.Nodes.Add(arcNode);
                    existingNodes[defNode.Id] = arcNode;
                }

                arcNode.Title = defNode.Title;
                arcNode.Description = BuildNodeDescription(defNode);
                var kindKey = defNode.Kind.ToString().ToLowerInvariant();
                if (!string.Equals(arcNode.Kind, kindKey, StringComparison.Ordinal) || arcNode.Icon is null)
                {
                    arcNode.Kind = kindKey;
                    arcNode.Icon = CreateNodeIcon(defNode.Kind);
                }

                // 全部使用单行 Standard 卡片与统一高度,避免横向排布时不同类型节点高低不齐
                arcNode.Appearance = ArcWorkflowNodeAppearance.Standard;
                arcNode.Size = defNode.Kind is WorkflowNodeKind.Trigger or WorkflowNodeKind.Join
                    ? new Size(216, 58)
                    : new Size(268, 58);

                if (runNodes is not null && runNodes.TryGetValue(defNode.Id, out var nr))
                {
                    arcNode.State = MapNodeState(nr.Status);
                    arcNode.Result = CleanNodeResult(nr);
                    arcNode.Error = CleanSingleLine(nr.Error, 64);
                    arcNode.Duration = nr.StartedAt is { } s && nr.FinishedAt is { } f && f >= s
                        ? TimeSpan.FromMilliseconds(f - s)
                        : null;
                }
                else
                {
                    arcNode.State = ArcWorkflowNodeState.Idle;
                    arcNode.Result = null;
                    arcNode.Error = null;
                    arcNode.Duration = null;
                }
            }

            // 增量同步边
            var existingEdges = _graph.Edges.ToDictionary(static e => e.Id, StringComparer.Ordinal);
            var desiredEdgeIds = new HashSet<string>(def.Edges.Select(static e => e.Id), StringComparer.Ordinal);
            for (var i = _graph.Edges.Count - 1; i >= 0; i--)
            {
                if (!desiredEdgeIds.Contains(_graph.Edges[i].Id))
                {
                    _graph.Edges.RemoveAt(i);
                }
            }

            foreach (var defEdge in def.Edges)
            {
                if (!existingNodes.TryGetValue(defEdge.Source, out var src) || !existingNodes.TryGetValue(defEdge.Target, out var dst))
                {
                    continue;
                }

                if (!existingEdges.TryGetValue(defEdge.Id, out var arcEdge))
                {
                    var srcPort = defEdge.SourcePort is { Length: > 0 } sp
                        ? src.Outputs.FirstOrDefault(p => string.Equals(p.Id, sp, StringComparison.Ordinal))
                        : null;
                    var dstPort = defEdge.TargetPort is { Length: > 0 } tp
                        ? dst.Inputs.FirstOrDefault(p => string.Equals(p.Id, tp, StringComparison.Ordinal))
                        : null;
                    arcEdge = new ArcWorkflowEdge(src, srcPort, dst, dstPort, defEdge.Id);
                    _graph.Edges.Add(arcEdge);
                }

                arcEdge.State = runEdges is not null && runEdges.TryGetValue(defEdge.Id, out var er)
                    ? MapEdgeState(er.Status)
                    : ArcWorkflowEdgeState.Auto;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private WorkflowDefinition ExtractDefinition(WorkflowDefinition baseline)
    {
        var baseNodes = baseline.Nodes.ToDictionary(static n => n.Id, StringComparer.Ordinal);
        var nodes = _graph.Nodes.Select(n =>
        {
            var pos = !double.IsNaN(n.Position.X) && !double.IsNaN(n.Position.Y)
                ? new WorkflowPosition(Math.Round(n.Position.X, 1), Math.Round(n.Position.Y, 1))
                : null;
            return baseNodes.TryGetValue(n.Id, out var orig)
                ? orig with { Title = n.Title, Position = pos }
                : new WorkflowNodeDef(n.Id, WorkflowNodeKind.Agent, n.Title, n.Description, pos);
        }).ToArray();

        var edges = _graph.Edges.Select(e => new WorkflowEdgeDef(
            e.Id,
            e.Source.Id,
            e.Target.Id,
            e.SourcePort?.Id,
            e.TargetPort?.Id)).ToArray();

        return baseline with { Nodes = nodes, Edges = edges };
    }

    private static ArcWorkflowNodeState MapNodeState(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Pending => ArcWorkflowNodeState.Pending,
        WorkflowNodeRunStatus.Running or WorkflowNodeRunStatus.WaitingApproval => ArcWorkflowNodeState.Running,
        WorkflowNodeRunStatus.Done => ArcWorkflowNodeState.Done,
        WorkflowNodeRunStatus.Failed => ArcWorkflowNodeState.Failed,
        WorkflowNodeRunStatus.Skipped or WorkflowNodeRunStatus.Cancelled => ArcWorkflowNodeState.Skipped,
        _ => ArcWorkflowNodeState.Idle,
    };

    private static ArcWorkflowEdgeState MapEdgeState(WorkflowEdgeRunStatus status) => status switch
    {
        WorkflowEdgeRunStatus.Active => ArcWorkflowEdgeState.Active,
        WorkflowEdgeRunStatus.Skipped => ArcWorkflowEdgeState.Skipped,
        _ => ArcWorkflowEdgeState.Auto,
    };

    private static LucideIcon CreateNodeIcon(WorkflowNodeKind kind) => new()
    {
        Kind = kind switch
        {
            WorkflowNodeKind.Agent => LucideIconKind.Bot,
            WorkflowNodeKind.Tool => LucideIconKind.Terminal,
            WorkflowNodeKind.Condition => LucideIconKind.GitBranch,
            WorkflowNodeKind.Approval => LucideIconKind.ShieldCheck,
            WorkflowNodeKind.Join => LucideIconKind.GitMerge,
            WorkflowNodeKind.Output => LucideIconKind.CheckCheck,
            WorkflowNodeKind.Trigger => LucideIconKind.Zap,
            WorkflowNodeKind.Plugin => LucideIconKind.Puzzle,
            _ => LucideIconKind.Workflow,
        },
        Size = 15,
    };

    private static string BuildNodeDescription(WorkflowNodeDef node)
    {
        if (CleanSingleLine(node.Description, 44) is { Length: > 0 } desc)
        {
            return desc;
        }

        var kindLabel = NcwTexts.Get(node.Kind switch
        {
            WorkflowNodeKind.Agent => NcwTextKind.WorkflowNodeKindAgent,
            WorkflowNodeKind.Tool => NcwTextKind.WorkflowNodeKindTool,
            WorkflowNodeKind.Condition => NcwTextKind.WorkflowNodeKindCondition,
            WorkflowNodeKind.Approval => NcwTextKind.WorkflowNodeKindApproval,
            WorkflowNodeKind.Join => NcwTextKind.WorkflowNodeKindJoin,
            WorkflowNodeKind.Output => NcwTextKind.WorkflowNodeKindOutput,
            WorkflowNodeKind.Trigger => NcwTextKind.WorkflowNodeKindTrigger,
            WorkflowNodeKind.Plugin => NcwTextKind.WorkflowNodeKindPlugin,
            _ => NcwTextKind.Workflows,
        });

        var detail = node.Kind switch
        {
            WorkflowNodeKind.Tool => CleanSingleLine(node.Config?.ToolName, 28),
            WorkflowNodeKind.Condition => CleanSingleLine(node.Config?.Expression, 36),
            WorkflowNodeKind.Approval => CleanSingleLine(node.Config?.Prompt, 36),
            WorkflowNodeKind.Agent => CleanSingleLine(node.Config?.Model, 28),
            _ => null,
        };

        return detail is { Length: > 0 } ? $"{kindLabel} · {detail}" : kindLabel;
    }

    private static string? CleanNodeResult(WorkflowNodeRunRecord nr)
    {
        // 优先取有实际内容的输出摘要;跳过通用的占位摘要(如 "Tool succeeded" / "Completed")
        var rawOutput = CleanSingleLine(nr.OutputText, 44);
        var rawSummary = CleanSingleLine(nr.Summary, 44);
        if (rawOutput is { Length: > 0 }
            && (rawSummary is null || rawSummary is "Tool succeeded" or "Completed" or "Done"))
        {
            return rawOutput;
        }

        return rawSummary ?? rawOutput;
    }

    private static string? CleanSingleLine(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 剥离工具 stdout/stderr 包装标签并折成单行
        var cleaned = text
            .Replace("<stdout>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</stdout>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("<stderr>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</stderr>", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (cleaned.Length == 0)
        {
            return null;
        }

        // 取第一条非空行
        foreach (var rawLine in cleaned.AsSpan().EnumerateLines())
        {
            var trimmed = rawLine.Trim();
            if (trimmed.IsEmpty)
            {
                continue;
            }

            return trimmed.Length <= maxLength
                ? trimmed.ToString()
                : string.Concat(trimmed[..(maxLength - 1)], "…");
        }

        return null;
    }
}
