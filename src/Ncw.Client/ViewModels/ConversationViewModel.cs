using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 当前打开的会话(栏 3 的内容):转录、待办、目标条、输入框。
/// 一切事实都从 <see cref="ClientStores"/> 读(会话镜像、Run、交互、子代理、目标、模型候选),这里只在 Store 变化后重新派生
/// (<see cref="TranscriptBuilder"/>)并按行 key 对齐到已有的行视图模型上;ViewModel 不订阅任何协议事件。
/// 本类持有的只有纯 UI 状态:展开 / 折叠、草稿、「已在另一台设备上处理」的提示。
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IConversationCommands _commands;
    private readonly SubagentActions? _subagents;
    private readonly GoalActions? _goals;
    private readonly IMessageCommands? _messages;
    private readonly IImageSource? _images;
    private readonly TimeProvider _clock;
    private readonly Ncw.Client.Widgets.WidgetHostContext? _widgets;
    private string _lastUserText = "";
    private SessionRef? _session;
    private string? _pendingFocus;

    public ConversationViewModel(ClientStores stores, IConversationCommands commands, SubagentActions? subagents = null, GoalActions? goals = null, TimeProvider? clock = null, IComposerLookups? lookups = null,
        AttachmentTrayViewModel? attachments = null, Ncw.Client.Widgets.WidgetHostContext? widgets = null, IMessageCommands? messages = null, IImageSource? images = null)
    {
        _messages = messages;
        _images = images;
        _widgets = widgets;
        _stores = stores;
        _commands = commands;
        _subagents = subagents;
        _goals = goals;
        _clock = clock ?? TimeProvider.System;
        Composer = new ComposerViewModel(commands, lookups, attachments: attachments);
        stores.Conversations.Changed += Refresh;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        stores.Providers.Changed += Refresh;
        Refresh();
    }

    public ComposerViewModel Composer { get; }

    /// <summary>转录的行(按 key 对齐,原地更新)。视图用虚拟化的列表显示。</summary>
    public ObservableCollection<RowViewModel> Rows { get; } = [];

    /// <summary>别的设备抢先处理了的交互(纯 UI 提示,用户关掉就没了)。</summary>
    public ObservableCollection<HandledNotice> Notices { get; } = [];

    [ObservableProperty] public partial bool HasSession { get; private set; }

    [ObservableProperty] public partial string Title { get; private set; } = "";

    /// <summary>已经选中了会话但它的快照还没到(订阅中)。</summary>
    [ObservableProperty] public partial bool IsLoading { get; private set; }

    /// <summary>设备断开:运行中的东西状态未知(规则 7),界面显示「连接中…」,不判结束、不继续计时。</summary>
    [ObservableProperty] public partial bool IsDeviceUnknown { get; private set; }

    [ObservableProperty] public partial bool IsRunning { get; private set; }

    /// <summary>
    /// 消息工具条的编辑 / 分支 / 删除能不能用:接了 <see cref="MessageActions"/>、设备连着(断开时状态未知,规则 7)、会话没有 Run 在跑(设备会拒绝改写历史)、
    /// 角色不是只读。复制不受它限制。
    /// </summary>
    [ObservableProperty] public partial bool CanEditHistory { get; private set; }

    /// <summary>消息操作失败的稳定 key(<see cref="MessageErrorKeys"/>),界面翻译;null = 没有错误。用户关掉或下一次操作开始时清掉。</summary>
    [ObservableProperty] public partial string? MessageError { get; private set; }

    public bool HasMessageError => MessageError is not null;

    /// <summary>灯箱里放大显示的图片;null = 没有打开。纯 UI 状态,换会话时关掉。</summary>
    [ObservableProperty] public partial ImageThumbViewModel? Lightbox { get; private set; }

    public bool HasLightbox => Lightbox is not null;

    partial void OnLightboxChanged(ImageThumbViewModel? value) => OnPropertyChanged(nameof(HasLightbox));

    [RelayCommand]
    private void OpenImage(ImageThumbViewModel image)
    {
        if (image.IsReady)
        {
            Lightbox = image;
        }
    }

    [RelayCommand]
    private void CloseLightbox() => Lightbox = null;

    /// <summary>上下文占用 0–100;没有数据时为 0(<see cref="HasContext"/> 为假)。</summary>
    [ObservableProperty] public partial int ContextPercent { get; private set; }

    [ObservableProperty] public partial bool HasContext { get; private set; }

    [ObservableProperty] public partial TodoPanelViewModel? Todo { get; private set; }

    [ObservableProperty] public partial GoalBarViewModel? Goal { get; private set; }

    /// <summary>有没有随时间变化的行(运行中的子代理要计时):视图据此决定要不要开每秒一次的定时器,定时器只调 <see cref="Tick"/>。</summary>
    [ObservableProperty] public partial bool NeedsTick { get; private set; }

    public bool IsEmpty => HasSession && !IsLoading && Rows.Count == 0;

    public bool HasNotices => Notices.Count > 0;

    public SessionRef? Session => _session;

    /// <summary>要求视图滚到并高亮某一行(收件箱点进来定位到交互卡片)。行已经在就立刻触发,还没到(快照在路上)就等它出现。</summary>
    public event Action<RowViewModel>? FocusRequested;

    /// <summary>
    /// 请求定位到一行(转录行的稳定 key,例如交互卡片是 <c>i:交互 id</c>);传 null 清掉待定的请求。
    /// 高亮是纯 UI 状态(<see cref="InteractionRowViewModel.IsHighlighted"/>),换会话时自动清掉。
    /// </summary>
    public void RequestFocus(string? rowKey)
    {
        _pendingFocus = rowKey;
        foreach (var row in Rows.OfType<InteractionRowViewModel>())
        {
            row.IsHighlighted = false;
        }

        TryFocus();
    }

    private void TryFocus()
    {
        if (_pendingFocus is not { } key || Rows.FirstOrDefault(r => r.Key == key) is not { } row)
        {
            return;
        }

        _pendingFocus = null;
        if (row is InteractionRowViewModel interaction)
        {
            interaction.IsHighlighted = true;
        }

        FocusRequested?.Invoke(row);
    }

    /// <summary>定时器每秒调一次:重新派生一遍,运行中子代理的耗时随时间前进。</summary>
    public void Tick() => Refresh();

    private string DeviceName(string deviceId) =>
        _stores.Workspace.State.Devices.TryGetValue(deviceId, out var device) ? device.Name : deviceId;

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        var conversations = _stores.Conversations.State;

        if (selection.Project is not { } project || selection.SessionId is not { } sessionId)
        {
            _session = null;
            HasSession = false;
            Title = "";
            Reconcile([]);
            Todo = null;
            Goal = null;
            IsLoading = false;
            IsRunning = false;
            CanEditHistory = false;
            HasContext = false;
            ContextPercent = 0;
            NeedsTick = false;
            Composer.Update(null, null, null, null, SessionActivity.Idle, DeviceConnection.Connecting, null, []);
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var session = new SessionRef(project.DeviceId, sessionId);
        if (_session != session)
        {
            Notices.Clear();
            OnPropertyChanged(nameof(HasNotices));
            _pendingFocus = null;
            Lightbox = null;
        }

        _session = session;
        workspace.Devices.TryGetValue(project.DeviceId, out var device);
        var connection = device?.Connection ?? DeviceConnection.Connecting;
        var unknown = connection != DeviceConnection.Connected;
        var view = conversations.Sessions.GetValueOrDefault(session);
        var summary = workspace.Sessions.FirstOrDefault(s => s.Id == sessionId && s.Project == project);

        HasSession = true;
        Title = summary?.Title ?? view?.Meta.Title ?? "";
        IsLoading = view is null && !unknown;
        IsDeviceUnknown = unknown;

        var info = workspace.Projects.GetValueOrDefault(project);
        var fallback = info is null ? null : new EnvRefDto(project.DeviceId, info.EnvironmentName ?? "local", info.EnvironmentName ?? "local", info.Environment == EnvironmentKind.Ssh ? "ssh" : "local");
        var rows = TranscriptBuilder.Build(conversations, session, fallback, _clock.GetUtcNow(), unknown);
        _lastUserText = rows.OfType<UserRow>().LastOrDefault()?.Text ?? _lastUserText;
        Reconcile(rows);
        TryFocus();

        var activity = SessionActivityView.Of(sessionId, device?.Index, connection);
        IsRunning = activity == SessionActivity.Running;
        CanEditHistory = _messages is not null && !unknown && !IsRunning && device?.Role is not "viewer";
        MarkRegenerable();
        NeedsTick = !unknown && rows.OfType<SubagentRow>().Any(r => r.View.IsActive);

        Todo = view?.Todo is { } todo && !(Todo?.View.Equals(todo) ?? false) ? new TodoPanelViewModel(todo) : view?.Todo is null ? null : Todo;
        var goal = conversations.GoalOf(session);
        Goal = goal is null ? null : new GoalBarViewModel(goal, unknown, ClearGoalCommand);

        var run = conversations.Runs.Values.Where(r => r.Ref.DeviceId == project.DeviceId && r.SessionId == sessionId && r.Context is not null)
            .OrderByDescending(r => r.Ref.RunId, StringComparer.Ordinal).FirstOrDefault();
        HasContext = run?.Context is { Window: > 0 };
        ContextPercent = run?.Context is { Window: > 0 } ctx ? (int)Math.Clamp(Math.Round(100.0 * ctx.Used / ctx.Window), 0, 100) : 0;

        Composer.Update(session, view?.Meta, view?.Queue, view?.ActiveRunId ?? ActiveRunOf(conversations, session), activity, connection, device?.Role,
            _stores.Providers.State.Of(project.DeviceId).Options);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string? ActiveRunOf(ConversationState state, SessionRef session) =>
        state.Runs.Values.Where(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId && r.Phase == RunPhase.Running)
            .Select(r => r.Ref.RunId).FirstOrDefault();

    // ───────────────────────── 行对齐 ─────────────────────────

    private void Reconcile(ImmutableArray<TranscriptRow> rows)
    {
        var existing = new Dictionary<string, RowViewModel>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            existing.TryAdd(row.Key, row);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var target = 0;
        foreach (var row in rows)
        {
            if (!seen.Add(row.Key))
            {
                continue;
            }

            if (target < Rows.Count && Rows[target].Key == row.Key)
            {
                Apply(Rows[target], row);
            }
            else if (existing.TryGetValue(row.Key, out var vm) && Rows.IndexOf(vm) is var old and >= 0 && old >= target)
            {
                Rows.Move(old, target);
                Apply(vm, row);
            }
            else
            {
                Rows.Insert(target, Create(row));
            }

            target++;
        }

        while (Rows.Count > target)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    private static void Apply(RowViewModel vm, TranscriptRow row)
    {
        if (vm is InteractionRowViewModel interaction && row is InteractionRow ir)
        {
            interaction.Refresh(ir);
        }
        else
        {
            vm.Update(row);
        }
    }

    private RowViewModel Create(TranscriptRow row) => row switch
    {
        UserRow r => new UserRowViewModel(r, ImagesOf(r)),
        AssistantRow r => new AssistantRowViewModel(r),
        ThinkingRow r => new ThinkingRowViewModel(r),
        ToolRow r => new ToolRowViewModel(r, DeviceName),
        WidgetRow r => new WidgetRowViewModel(r, () => _session ?? default, _widgets, DeviceName),
        SubagentRow r => new SubagentRowViewModel(r, StopSubagentCommand, OpenSubagentCommand),
        SubagentReportRow r => new ReportRowViewModel(r),
        CompactRow r => new CompactRowViewModel(r),
        ErrorRow r => new ErrorRowViewModel(r, RetryCommand),
        GoalStatusRow r => new GoalStatusRowViewModel(r),
        StatusRow r => new StatusRowViewModel(r),
        InteractionRow r => new InteractionRowViewModel(r, _session?.DeviceId ?? "", _commands, DeviceName, OnHandled, _clock),
        _ => throw new InvalidOperationException($"Unknown transcript row {row.GetType().Name}"),
    };

    /// <summary>这条用户消息里的图片引用 → 缩略图(会话镜像里找消息;图片字节按需向设备取,不进 Store)。</summary>
    private IReadOnlyList<ImageThumbViewModel> ImagesOf(UserRow row)
    {
        if (_images is null || row.ImageCount == 0 || _session is not { } session
            || _stores.Conversations.State.Sessions.GetValueOrDefault(session) is not { } view
            || view.Messages.FirstOrDefault(m => m.Id == row.MessageId) is not { } message)
        {
            return [];
        }

        return [.. MessageImages.Of(message).Select(i => new ImageThumbViewModel(i.Mime, i.DataRef, ct => _images.LoadAsync(session.DeviceId, i.DataRef, ct)))];
    }

    private void OnHandled(HandledNotice notice)
    {
        if (Notices.All(n => n.InteractionId != notice.InteractionId))
        {
            Notices.Add(notice);
            OnPropertyChanged(nameof(HasNotices));
        }
    }

    // ───────────────────────── 意图 ─────────────────────────

    [RelayCommand]
    private void DismissNotice(HandledNotice notice)
    {
        Notices.Remove(notice);
        OnPropertyChanged(nameof(HasNotices));
    }

    /// <summary>失败的 Run:把最后一条用户消息再发一次(同一个发送入口,档位取输入框当前的)。</summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_session is { } session && _lastUserText.Length > 0)
        {
            await _commands.SendAsync(session, _lastUserText, Composer.Options());
        }
    }

    [RelayCommand]
    private async Task StopSubagentAsync(SubagentRef card)
    {
        if (_subagents is not null && _stores.Conversations.State.Subagents.TryGetValue(card, out var entry))
        {
            await _subagents.StopAsync(entry);
        }
    }

    /// <summary>点开子代理的完整记录(右侧只读面板接入前:订阅它的子会话,内容在 Store 里)。</summary>
    [RelayCommand]
    private async Task OpenSubagentAsync(SubagentRef card)
    {
        if (_subagents is not null && _stores.Conversations.State.Subagents.TryGetValue(card, out var entry))
        {
            await _subagents.OpenAsync(entry);
        }
    }

    // ───────────────────────── 消息操作(编辑 / 分支 / 删除一轮) ─────────────────────────
    // 一个意图一个方法(规则 4):工具条、快捷键都调这里;改写由设备执行,结果经 history.reset 回到 Store。

    partial void OnMessageErrorChanged(string? value) => OnPropertyChanged(nameof(HasMessageError));

    [RelayCommand]
    private void DismissMessageError() => MessageError = null;

    [RelayCommand]
    private void BeginEdit(UserRowViewModel row)
    {
        if (!CanEditHistory)
        {
            return;
        }

        MessageError = null;
        row.IsConfirmingDelete = false;
        row.EditText = row.Text;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit(UserRowViewModel row) => row.IsEditing = false;

    /// <summary>只改写这条提问的文字,后面的回复保留。</summary>
    [RelayCommand]
    private async Task SaveEditAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session || string.IsNullOrWhiteSpace(row.EditText))
        {
            return;
        }

        MessageError = null;
        var result = await _messages.EditAsync(session, row.MessageId, row.EditText.Trim());
        if (result.IsOk)
        {
            row.IsEditing = false;
        }
        else
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    /// <summary>编辑后从这里重新运行:这条和之后的全部移除,再用新文字发一次。发送失败时历史已截断,文字放回输入框。</summary>
    [RelayCommand]
    private async Task ResendEditAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session || string.IsNullOrWhiteSpace(row.EditText))
        {
            return;
        }

        MessageError = null;
        var text = row.EditText.Trim();
        var result = await _messages.EditAndResendAsync(session, row.MessageId, text, FilesOf(session, row.MessageId), Composer.Options());
        if (result.IsOk)
        {
            row.IsEditing = false;
            return;
        }

        if (result.ErrorKey == MessageActions.ResendFailedKey)
        {
            Composer.Draft = text; // 历史已经截断:别让用户丢掉这段文字
        }

        MessageError = MessageErrorKeys.Of(result);
    }

    /// <summary>
    /// 重新生成最近一轮的回复:用同一条提问(文字和附件都原样)重跑一次,旧回复被截断移除。和「编辑后重新运行」是同一个入口,只是文字不变。
    /// 只对最近一轮开放(更早的回复后面还有对话,重跑会把后面的全部截掉)。
    /// </summary>
    [RelayCommand]
    private async Task RegenerateAsync(AssistantRowViewModel row)
    {
        if (_messages is null || !CanEditHistory || !row.CanRegenerate || _session is not { } session
            || Rows.OfType<UserRowViewModel>().LastOrDefault() is not { } turn)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.EditAndResendAsync(session, turn.MessageId, turn.Text, FilesOf(session, turn.MessageId), Composer.Options());
        if (result.IsOk)
        {
            return;
        }

        if (result.ErrorKey == MessageActions.ResendFailedKey)
        {
            Composer.Draft = turn.Text; // 历史已经截断:原提问放回输入框,用户可以直接再发
        }

        MessageError = MessageErrorKeys.Of(result);
    }

    /// <summary>原消息的附件(会话镜像里找;找不到就没有附件)。</summary>
    private IReadOnlyList<InputFile> FilesOf(SessionRef session, string messageId) =>
        _stores.Conversations.State.Sessions.GetValueOrDefault(session)?.Messages.FirstOrDefault(m => m.Id == messageId) is { } message
            ? MessageInputs.FilesOf(message)
            : [];

    /// <summary>每次对齐行之后标记「重新生成」落在哪一段:最后一条用户提问之后、最后一段已写完的助手文字。</summary>
    private void MarkRegenerable()
    {
        var lastUser = -1;
        var lastAssistant = -1;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is UserRowViewModel)
            {
                lastUser = i;
            }
            else if (Rows[i] is AssistantRowViewModel)
            {
                lastAssistant = i;
            }
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is AssistantRowViewModel assistant)
            {
                assistant.CanRegenerate = i == lastAssistant && lastUser >= 0 && i > lastUser && !assistant.IsStreaming && !IsRunning;
            }
        }
    }

    /// <summary>从这一轮分支出新会话并打开它。</summary>
    [RelayCommand]
    private async Task BranchTurnAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session || _stores.Selection.State.Project is not { } project)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.BranchAsync(project, session.SessionId, row.MessageId);
        if (!result.IsOk)
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    [RelayCommand]
    private void AskDeleteTurn(UserRowViewModel row)
    {
        if (CanEditHistory)
        {
            row.IsEditing = false;
            row.IsConfirmingDelete = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteTurn(UserRowViewModel row) => row.IsConfirmingDelete = false;

    /// <summary>第二次确认之后删除这一轮。</summary>
    [RelayCommand]
    private async Task DeleteTurnAsync(UserRowViewModel row)
    {
        if (_messages is null || _session is not { } session)
        {
            return;
        }

        MessageError = null;
        var result = await _messages.DeleteTurnAsync(session, row.MessageId);
        row.IsConfirmingDelete = false;
        if (!result.IsOk)
        {
            MessageError = MessageErrorKeys.Of(result);
        }
    }

    [RelayCommand]
    private async Task ClearGoalAsync()
    {
        if (_goals is not null && _session is { } session)
        {
            await _goals.ClearAsync(session);
        }
    }
}
