using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 转录里一行的视图模型。身份是 <see cref="Key"/>:Store 变了之后 <see cref="ConversationViewModel"/> 重新派生出行,
/// 按 key 对齐到已有的这些对象上调 <see cref="Update"/>——展开 / 折叠这类纯 UI 状态留在这里,不会因为数据更新而丢。
/// </summary>
public abstract class RowViewModel(string key) : ObservableObject
{
    public string Key { get; } = key;

    /// <summary>用新派生出来的行更新自己;没有变化返回 false。</summary>
    public abstract bool Update(TranscriptRow row);
}

/// <summary>带一条派生数据 <typeparamref name="T"/> 的行。数据变了就通知全部属性(派生属性很多,逐个通知不值得)。</summary>
public abstract class RowViewModel<T>(T row) : RowViewModel(row.Key) where T : TranscriptRow
{
    public T Row { get; private set; } = row;

    public sealed override bool Update(TranscriptRow row)
    {
        if (row is not T next || Equals(Row, next))
        {
            return false;
        }

        Row = next;
        OnPropertyChanged(string.Empty);
        return true;
    }
}

public sealed partial class UserRowViewModel(UserRow row, IReadOnlyList<ImageThumbViewModel>? images = null) : RowViewModel<UserRow>(row)
{
    /// <summary>这条消息里的图片(缩略图;消息落库后不会变,所以随行创建一次)。没有引用可取时为空,界面退化成「N 张图片」。</summary>
    public IReadOnlyList<ImageThumbViewModel> Images { get; } = images ?? [];

    public bool HasThumbs => Images.Count > 0;

    /// <summary>有图片但取不到引用(比如旧快照):只显示张数。</summary>
    public bool HasImageCountOnly => Row.ImageCount > 0 && Images.Count == 0;

    /// <summary>正在编辑这条提问(纯 UI 状态:数据更新不会丢)。</summary>
    [ObservableProperty] public partial bool IsEditing { get; set; }

    /// <summary>编辑框里的文字。</summary>
    [ObservableProperty] public partial string EditText { get; set; } = "";

    /// <summary>点了「删除这一轮」、等第二次确认。</summary>
    [ObservableProperty] public partial bool IsConfirmingDelete { get; set; }

    public string MessageId => Row.MessageId;

    public string Text => Row.Text;

    public string FilesText => Row.FilesText;

    public bool HasFiles => Row.FilesText.Length > 0;

    public int ImageCount => Row.ImageCount;

    public bool HasImages => Row.ImageCount > 0;

    public DateTimeOffset At => Row.At;
}

public sealed partial class AssistantRowViewModel(AssistantRow row) : RowViewModel<AssistantRow>(row)
{
    /// <summary>这是最近一轮的最后一段回复、已经写完:可以「重新生成」。由 <see cref="ConversationViewModel"/> 在每次对齐行之后标记(取决于位置,不是行自己的数据)。</summary>
    [ObservableProperty] public partial bool CanRegenerate { get; set; }

    public string MessageId => Row.MessageId;

    public string Text => Row.Text;

    public bool IsStreaming => Row.IsStreaming;
}

/// <summary>思考块:默认折叠;展开与否是纯 UI 状态。</summary>
public sealed partial class ThinkingRowViewModel(ThinkingRow row) : RowViewModel<ThinkingRow>(row)
{
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Text => Row.Text;

    public bool IsStreaming => Row.IsStreaming;

    public int Seconds => Row.DurationMs is { } ms ? (int)Math.Max(1, Math.Round(ms / 1000.0)) : 0;

    public bool HasDuration => Row.DurationMs is not null;
}

public sealed record DiffLine(string Text, bool IsAdded, bool IsRemoved);

/// <summary>工具卡片。展开看摘要(diff 行 / 命令输出前几行)是纯 UI 状态;状态、环境标识、统计来自派生行。</summary>
public sealed partial class ToolRowViewModel(ToolRow row, Func<string, string> deviceName) : RowViewModel<ToolRow>(row)
{
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Name => Row.Name;

    public string Target => Row.Target;

    public ToolKind Kind => Row.Kind;

    public bool IsCommand => Row.Kind == ToolKind.Command;

    public bool IsEdit => Row.Kind is ToolKind.Edit or ToolKind.Write;

    public bool IsRead => Row.Kind == ToolKind.Read;

    public bool IsSearch => Row.Kind == ToolKind.Search;

    public bool IsOther => Row.Kind == ToolKind.Other;

    public bool IsRunning => Row.Status == ToolRowStatus.Running;

    public bool IsDone => Row.Status == ToolRowStatus.Done;

    public bool IsFailed => Row.Status == ToolRowStatus.Failed;

    public bool IsWaiting => Row.Status == ToolRowStatus.WaitingApproval;

    public bool IsInterrupted => Row.Status == ToolRowStatus.Interrupted;

    public bool IsQueued => Row.QueuedAhead is not null;

    public int QueuedAhead => Row.QueuedAhead ?? 0;

    /// <summary>「设备 · 环境」:在哪台设备的哪个环境里执行(AGENTS.md §5)。没有环境信息为 null。</summary>
    public string? EnvLabel => Row.Env is { } env ? $"{deviceName(env.DeviceId)} · {env.Name}" : null;

    public bool IsSsh => Row.Env?.Type == "ssh";

    public bool HasPreview => Row.Preview.Length > 0;

    public bool HasMore => Row.MoreLines > 0;

    public int MoreLines => Row.MoreLines;

    public bool HasStats => Row.Added > 0 || Row.Removed > 0;

    public bool HasAdded => Row.Added > 0;

    public bool HasRemoved => Row.Removed > 0;

    public string AddedText => $"+{Row.Added}";

    public string RemovedText => $"−{Row.Removed}";

    public IReadOnlyList<DiffLine> PreviewLines =>
        [.. Row.Preview.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => new DiffLine(l.Length > 1 ? l[1..] : "", l[0] == '+', l[0] == '-'))];

    /// <summary>可展开 = 有摘要可看。</summary>
    public bool CanExpand => HasPreview;
}

/// <summary>子代理卡片(转录里的那一张)。停止 / 转后台 / 点开是 <see cref="SubagentActions"/> 的意图,命令由 <see cref="ConversationViewModel"/> 接上。</summary>
public sealed class SubagentRowViewModel(SubagentRow row, IRelayCommand<SubagentRef> stop, IRelayCommand<SubagentRef> openIt) : RowViewModel<SubagentRow>(row)
{
    public SubagentViewState View => Row.View;

    public string Title => Row.View.AgentType.Length > 0 ? Row.View.AgentType : "Task";

    public string Description => Row.View.Description;

    public string ElapsedText => SubagentView.FormatElapsed(Row.View.Elapsed);

    public bool IsActive => Row.View.IsActive && !Row.Unknown;

    public bool IsUnknown => Row.Unknown;

    public bool IsTerminal => Row.View.IsTerminal;

    public bool IsError => Row.View.Status == SubagentStatus.Error;

    public bool IsAborted => Row.View.Status == SubagentStatus.Aborted;

    public bool IsDone => Row.View.Status == SubagentStatus.Done;

    public bool IsQueued => Row.View.Status == SubagentStatus.Queued;

    public bool CanStop => Row.View.CanStop && !Row.Unknown;

    public bool CanOpen => Row.View.CanOpen;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Row.Summary);

    public string? Summary => Row.Summary;

    public bool IsBackground => Row.View.Background;

    public SubagentRef Ref => Row.Ref;

    public IRelayCommand<SubagentRef> StopCommand { get; } = stop;

    public IRelayCommand<SubagentRef> OpenCommand { get; } = openIt;
}

public sealed class ReportRowViewModel(SubagentReportRow row) : RowViewModel<SubagentReportRow>(row)
{
    public string? Summary => Row.Summary;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Row.Summary);
}

public sealed partial class CompactRowViewModel(CompactRow row) : RowViewModel<CompactRow>(row)
{
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Summary => Row.Summary;

    public bool IsAuto => Row.Trigger != "manual";

    public int PreTokens => Row.PreTokens;

    public int PostTokens => Row.PostTokens;

    public string PreText => ComposerLogic.WindowText(Row.PreTokens);

    public string PostText => ComposerLogic.WindowText(Row.PostTokens);
}

public sealed class ErrorRowViewModel(ErrorRow row, IAsyncRelayCommand retry) : RowViewModel<ErrorRow>(row)
{
    public string Code => Row.Code;

    public string Message => Row.Message;

    public string? MessageKey => Row.MessageKey;

    public bool CanRetry => Row.CanRetry && Row.Retryable;

    public IAsyncRelayCommand RetryCommand { get; } = retry;
}

public sealed class GoalStatusRowViewModel(GoalStatusRow row) : RowViewModel<GoalStatusRow>(row)
{
    public string Condition => Row.Condition;

    public string? Reason => Row.Reason;

    public bool HasReason => !string.IsNullOrWhiteSpace(Row.Reason);

    public bool IsMet => Row.Met;

    public bool IsFailed => Row.Failed;

    public bool IsCleared => Row.Cleared;

    public bool IsSet => Row.Set;

    /// <summary>既不是达成、失败、清除、设定:一次「未达成」的判定。</summary>
    public bool IsNotMet => !Row.Met && !Row.Failed && !Row.Cleared && !Row.Set;
}

public sealed class StatusRowViewModel(StatusRow row) : RowViewModel<StatusRow>(row)
{
    public RunStatusKind Kind => Row.Kind;

    public bool IsRetrying => Row.Kind == RunStatusKind.Retrying;

    public bool IsCompacting => Row.Kind == RunStatusKind.Compacting;

    public bool IsUnknown => Row.Kind == RunStatusKind.Unknown;

    public int Attempt => Row.Attempt;
}

/// <summary>输入框上方的待办清单。数据来自 SessionView.Todo(Core 的 <see cref="Todos"/> 从转录派生)。</summary>
public sealed partial class TodoPanelViewModel(TodoView view) : ObservableObject
{
    public TodoView View { get; } = view;

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    public IReadOnlyList<TodoRowView> Items { get; } = [.. view.Items.Select(i => new TodoRowView(i.Content, i.ActiveForm, i.Status))];

    public int Completed => View.Completed;

    public int Total => View.Total;

    public string? Current => View.Current?.ActiveForm;

    public bool IsComplete => View.IsComplete;
}

public sealed record TodoRowView(string Content, string ActiveForm, TodoStatus Status)
{
    public bool IsDone => Status == TodoStatus.Completed;

    public bool IsActive => Status == TodoStatus.InProgress;

    public bool IsPending => Status == TodoStatus.Pending;

    /// <summary>进行中显示「正在……」,其余显示内容。</summary>
    public string Text => Status == TodoStatus.InProgress && ActiveForm.Length > 0 ? ActiveForm : Content;
}

/// <summary>输入框上方的目标条。</summary>
public sealed partial class GoalBarViewModel(ActiveGoal goal, bool unknown, IAsyncRelayCommand clear) : ObservableObject
{
    public string Condition => goal.Condition;

    public int Iterations => goal.Iterations;

    public bool HasIterations => goal.Iterations > 0;

    public string? LastReason => goal.LastReason;

    public bool HasLastReason => !string.IsNullOrWhiteSpace(goal.LastReason);

    public bool IsDeferred => goal.DeferredSince is not null;

    public bool IsUnknown => unknown;

    public long Tokens => goal.Tokens;

    public IAsyncRelayCommand ClearCommand { get; } = clear;
}

/// <summary>
/// 内联可视化卡片的视图模型。阶段、卡片、快照文字都来自派生行(<see cref="WidgetRow"/>,阶段由 Core 的 <see cref="WidgetView"/> 派生);
/// 状态镜像(小组件状态、提交进度)在 <c>ClientStores.Widgets</c> 里——视图把这一行贴到屏幕上(<see cref="Attach"/>)期间才订阅它,离开屏幕(虚拟化回收)就退订,所以不会每行常驻一个订阅。
/// 本类持有的纯 UI 状态:没有(展开 / 折叠在视图里)。
/// </summary>
public sealed class WidgetRowViewModel(WidgetRow row, Func<SessionRef> session, WidgetHostContext? host, Func<string, string> deviceName) : RowViewModel<WidgetRow>(row)
{
    private int _attached;

    public SessionRef Session => session();

    public WidgetRef Ref => new(Session.DeviceId, Session.SessionId, Row.CallId);

    public WidgetCard? Card => Row.Card;

    public string Title => Row.Title;

    public string ToolName => Row.ToolName;

    public WidgetPhase Phase => Row.Phase;

    public bool IsLoading => Row.Phase == WidgetPhase.Loading;

    public bool IsReady => Row.Phase is WidgetPhase.Ready or WidgetPhase.Expired;

    public bool IsExpired => Row.Phase == WidgetPhase.Expired;

    public bool IsFailed => Row.Phase == WidgetPhase.Failed;

    public bool IsUnknown => Row.Phase == WidgetPhase.Unknown;

    public bool HasCard => Row.Card is not null;

    /// <summary>还没有卡片就过期(调用没有回执而 Run 已经不在了):画占位;有卡片时过期只是个标记,内容照常显示。</summary>
    public bool ShowExpiredPlaceholder => IsExpired && !HasCard;

    /// <summary>设备断开而卡片还没出来:画「状态未知」占位。已经有卡片时内容照常显示,只在卡片头上标记。</summary>
    public bool ShowUnknownPlaceholder => IsUnknown && !HasCard;

    /// <summary>有卡片而设备断开且状态靠设备推进(等提交):卡片头上的「状态未知」标记。</summary>
    public bool ShowUnknownBadge => IsUnknown && HasCard;

    /// <summary>有卡片、没到未知、但设备断开了:桥不能调设备(「离线」标记)。</summary>
    public bool ShowOfflineBadge => IsOffline && HasCard && !IsUnknown;

    public bool ShowExpiredBadge => IsExpired && HasCard;

    /// <summary>设备断开(无论卡片是定稿的还是还在等):桥不能调设备,界面标「离线」。</summary>
    public bool IsOffline => Row.Facts.DeviceUnknown;

    public bool Interactive => Row.Facts.Interactive;

    public bool CanSubmit => WidgetView.CanSubmit(Row.Facts);

    public string LoadingText => Row.LoadingText;

    public string? DataJson => Row.DataJson;

    /// <summary>「设备 · 环境」:在哪台设备的哪个环境里生成的(AGENTS.md §5)。</summary>
    public string? EnvLabel => Row.Env is { } env ? $"{deviceName(env.DeviceId)} · {env.Name}" : null;

    public bool IsSsh => Row.Env?.Type == "ssh";

    /// <summary>手机端 / WebView 不可用时的降级内容:从静态快照抽出来的文字。</summary>
    public string SnapshotText => WidgetSnapshotText.Extract(Row.Card?.Snapshot);

    public bool HasSnapshotText => SnapshotText.Length > 0;

    /// <summary>消毒拿掉过东西(界面提示一下)。</summary>
    public bool HasFindings => Row.Card is { Findings.Length: > 0 };

    public int FindingCount => Row.Card?.Findings.Sum(f => f.Count) ?? 0;

    /// <summary>有宿主能力(桌面装配了桥);否则只读(单测 / 没接设备)。</summary>
    public bool HasHost => host is not null;

    /// <summary>镜像里这张卡的状态(没读过 = <c>Loaded</c> 为假)。</summary>
    public WidgetEntry Entry => host?.Stores.Widgets.State.Of(Ref) ?? WidgetEntry.Empty;

    public event Action? EntryChanged;

    /// <summary>视图把卡贴到屏幕上:订阅镜像,并在需要时读一次状态(没读过、陈旧了、设备在线)。</summary>
    public void Attach()
    {
        if (host is null || Interlocked.Increment(ref _attached) != 1)
        {
            return;
        }

        host.Stores.Widgets.Changed += OnMirror;
        _ = LoadStateIfNeededAsync();
    }

    public void Detach()
    {
        if (host is null || Interlocked.Decrement(ref _attached) != 0)
        {
            return;
        }

        host.Stores.Widgets.Changed -= OnMirror;
    }

    /// <summary>设备重新在线 / 卡片刚就绪时,状态没读过或陈旧就重读(只在有卡片、设备在线时)。</summary>
    public Task LoadStateIfNeededAsync()
    {
        if (host is null || Row.Card is null || Row.Facts.DeviceUnknown)
        {
            return Task.CompletedTask;
        }

        var entry = Entry;
        return entry.Loaded && !entry.Stale ? Task.CompletedTask : host.Actions.GetStateAsync(Session.DeviceId, Session.SessionId, Row.CallId);
    }

    /// <summary>为视图里的 WebView 造桥(<paramref name="post"/>:把消息 JSON 送进页面)。没有宿主返回 null。</summary>
    public WidgetBridge? CreateBridge(Action<string> post) =>
        host?.CreateBridge(Ref, () => new WidgetBridgeContext(Row.Facts with { Submitted = Row.Facts.Submitted || Entry.Submit == WidgetSubmit.Accepted, AwaitingSubmit = Row.Facts.AwaitingSubmit && Entry.Submit != WidgetSubmit.Closed }, Row.Card?.Actions ?? []), post);

    /// <summary>这一刻要推给页面的环境(状态来自镜像,数据来自转录里按标题认领的最新一份)。</summary>
    public WidgetEnvironment Environment()
    {
        var entry = Entry;
        return new WidgetEnvironment(Row.Title, entry.StateJson, Row.DataJson, Row.Facts.Interactive, Row.Facts.Submitted || entry.Submit == WidgetSubmit.Accepted, Row.Card?.Actions ?? []);
    }

    private void OnMirror() => EntryChanged?.Invoke();
}
