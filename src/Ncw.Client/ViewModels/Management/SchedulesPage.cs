using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>运行的状态是否已经结束(终态压过运行中,规则 6)。</summary>
public static class ScheduleRunStatus
{
    public static bool IsActive(string status) => status is "queued" or "running";

    public static bool IsTerminal(string status) => !IsActive(status);
}

/// <summary>运行记录的一行。</summary>
public sealed partial class ScheduleRunRowViewModel(SchedulesViewModel owner, ScheduledRunDto dto) : ObservableObject
{
    public ScheduledRunDto Dto { get; } = dto;

    public string Id => Dto.Id;

    public string Status => Dto.Status;

    public bool IsActive => ScheduleRunStatus.IsActive(Dto.Status);

    public string Trigger => Dto.Trigger;

    public DateTimeOffset ScheduledAt => DateTimeOffset.FromUnixTimeMilliseconds(Dto.ScheduledAt).ToLocalTime();

    public TimeSpan? Duration => Dto.StartedAt is { } s && Dto.EndedAt is { } e ? TimeSpan.FromMilliseconds(Math.Max(0, e - s)) : null;

    public string? Summary => Dto.Summary;

    public string? ErrorKey => Dto.Error;

    public string SessionId => Dto.SessionId;

    [RelayCommand]
    private Task SelectAsync() => owner.SelectRunAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteRunAsync(this);
}

/// <summary>任务清单里的一行。</summary>
public sealed partial class ScheduleTaskRowViewModel(SchedulesViewModel owner, ScheduledTaskDto dto, bool isRunning, ScheduledRunDto? lastRun) : ObservableObject
{
    public ScheduledTaskDto Dto { get; } = dto;

    public string Id => Dto.Id;

    public string Name => Dto.Name;

    public string Prompt => Dto.Prompt;

    public string Kind => Dto.Schedule.Kind;

    public string? Time => Dto.Schedule.Time;

    public string? At => Dto.Schedule.At;

    public int[] Weekdays => Dto.Schedule.Weekdays ?? [];

    public bool RepeatEnabled => Dto.RepeatWindow.Enabled;

    public string? RepeatEnd => Dto.RepeatWindow.EndTime;

    public int? RepeatInterval => Dto.RepeatWindow.IntervalMinutes;

    public string Timezone => Dto.Timezone;

    public bool Enabled => Dto.Enabled;

    public string Model => Dto.Model;

    public bool CreatedByAgent => Dto.CreatedBy == "agent";

    /// <summary>运行中只由 schedules 主题里活动的运行回答(一个事实一个真源)。</summary>
    public bool IsRunning { get; } = isRunning;

    public ScheduledRunDto? LastRun { get; } = lastRun;

    public string? LastStatus => LastRun?.Status;

    /// <summary>下一次触发(本地时间);停用或已过期为 null。</summary>
    public DateTimeOffset? NextRunAt => Dto.Enabled && Dto.NextRunAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime() : null;

    [RelayCommand]
    private Task ToggleAsync() => owner.SetEnabledAsync(this, !Dto.Enabled);

    [RelayCommand]
    private Task RunNowAsync() => owner.RunNowAsync(this);

    [RelayCommand]
    private void Edit() => owner.Edit(this);

    [RelayCommand]
    private void AskDelete() => owner.AskDelete(this);

    [RelayCommand]
    private Task ShowRunsAsync() => owner.SelectTaskAsync(this);
}

/// <summary>编辑器里的星期开关(0 = 周日 … 6 = 周六)。</summary>
public sealed partial class WeekdayToggle(int day, bool isOn) : ObservableObject
{
    public int Day { get; } = day;

    [ObservableProperty] public partial bool IsOn { get; set; } = isOn;
}

/// <summary>
/// 新建 / 编辑定时任务的表单。规则按 2.x:一次(日期 + 时间)/ 每天(时间)/ 每周几(星期 + 时间),加可选的「当天窗口内重复」(到几点、每隔几分钟),按任务自己的时区求值。
/// 校验与「接下来 5 次触发」预览都是 Core 的纯函数(<see cref="ScheduleValidation"/>,和设备同一套),所以预览与设备实际会触发的时刻一致。
/// </summary>
public sealed partial class ScheduleEditorViewModel : ObservableObject
{
    public const int PreviewCount = 5;
    public static readonly IReadOnlyList<string> Kinds = [ScheduleRuleKinds.Once, ScheduleRuleKinds.Daily, ScheduleRuleKinds.Weekly];

    private readonly TimeProvider _clock;

    public ScheduleEditorViewModel(TimeProvider clock)
    {
        _clock = clock;
        for (var d = 0; d < 7; d++)
        {
            var toggle = new WeekdayToggle(d, d is >= 1 and <= 5);
            toggle.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Problems));
            Weekdays.Add(toggle);
        }
    }

    public ObservableCollection<WeekdayToggle> Weekdays { get; } = [];

    public ObservableCollection<ChoiceItem> ModelChoices { get; } = [];

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial bool IsNew { get; set; } = true;

    [ObservableProperty] public partial string? TaskId { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Prompt { get; set; } = "";

    /// <summary>「模型 + 供应商」下拉 key;空串 = 用项目的默认模型。</summary>
    [ObservableProperty] public partial string ModelKey { get; set; } = "";

    [ObservableProperty] public partial string Kind { get; set; } = ScheduleRuleKinds.Daily;

    /// <summary>一次性任务的日期 <c>YYYY-MM-DD</c>。</summary>
    [ObservableProperty] public partial string Date { get; set; } = "";

    /// <summary>开始时间 <c>HH:MM</c>(一次 / 每天 / 每周几都用)。</summary>
    [ObservableProperty] public partial string Time { get; set; } = "09:00";

    [ObservableProperty] public partial string Timezone { get; set; } = ScheduleMath.SystemTimeZone();

    [ObservableProperty] public partial bool RepeatEnabled { get; set; }

    [ObservableProperty] public partial string RepeatEnd { get; set; } = "18:00";

    [ObservableProperty] public partial double RepeatInterval { get; set; } = 60;

    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    [ObservableProperty] public partial Failure? SaveFailure { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    internal void Refresh() => OnPropertyChanged(nameof(CanSave));

    public bool IsOnce => Kind == ScheduleRuleKinds.Once;

    public bool IsWeekly => Kind == ScheduleRuleKinds.Weekly;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Problems) or nameof(Preview) or nameof(CanSave) or nameof(IsOnce) or nameof(IsWeekly) or nameof(Rule) or nameof(Repeat))
        {
            return;
        }

        OnPropertyChanged(nameof(IsOnce));
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanSave));
    }

    public ScheduleRuleSpec Rule => new(
        Kind,
        IsOnce ? $"{Date.Trim()}T{Time.Trim()}" : null,
        IsOnce ? null : Time.Trim(),
        IsWeekly ? [.. Weekdays.Where(w => w.IsOn).Select(w => w.Day)] : null);

    public RepeatSpec Repeat => new(RepeatEnabled, RepeatEnabled ? RepeatEnd.Trim() : null, RepeatEnabled ? (int)RepeatInterval : null);

    public IReadOnlyList<ScheduleProblem> Problems => ScheduleValidation.Validate(Name, Prompt, Rule, Timezone.Trim(), Repeat, _clock.GetUtcNow().ToUnixTimeMilliseconds());

    public bool CanSave => !IsSaving && Problems.Count == 0;

    /// <summary>接下来最多 5 次触发(任务时区的本地时间);规则不合法时为空。</summary>
    public IReadOnlyList<DateTimeOffset> Preview
    {
        get
        {
            var zone = ScheduleMath.FindZone(Timezone);
            if (zone is null)
            {
                return [];
            }

            var times = ScheduleValidation.Preview(Rule, Timezone.Trim(), Repeat, _clock.GetUtcNow().ToUnixTimeMilliseconds(), PreviewCount);
            return [.. times.Select(t => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(t), zone))];
        }
    }
}

/// <summary>
/// 侧栏「定时任务」页(docs/design/03 §1):当前项目的任务列表(<see cref="ClientStores.Schedules"/> 的镜像,<c>schedules</c> 主题全量权威)、新建 / 编辑、启停、立即运行、运行记录与详情、删除确认。
/// 定时任务无人值守地以 full 权限运行,所以<b>所有写入只有 owner</b>(member / viewer 只读,按钮禁用并说明原因)。运行中只由主题里活动的运行回答;设备断开 = 未知。
/// </summary>
public sealed partial class SchedulesViewModel : ManagementPageViewModel
{
    private readonly ClientStores _stores;
    private readonly ISchedulesCommands _commands;
    private readonly TimeProvider _clock;
    private readonly List<ScheduledRunDto> _history = [];
    private bool _historyHasMore;
    private string? _lastSession;

    public SchedulesViewModel(ClientStores stores, ISchedulesCommands commands, TimeProvider clock) : this(new SettingsScope(stores), stores, commands, clock)
    {
    }

    private SchedulesViewModel(SettingsScope scope, ClientStores stores, ISchedulesCommands commands, TimeProvider clock) : base(scope, Need.Viewer)
    {
        _stores = stores;
        _commands = commands;
        _clock = clock;
        Editor = new ScheduleEditorViewModel(clock);
        stores.Schedules.Changed += Sync;
        stores.Selection.Changed += FollowSelection;
        stores.Providers.Changed += SyncModels;
        stores.Workspace.Changed += RaiseGates;
        FollowSelection();
    }

    public ScheduleEditorViewModel Editor { get; }

    public ObservableCollection<ScheduleTaskRowViewModel> Tasks { get; } = [];

    public ObservableCollection<ScheduleRunRowViewModel> Runs { get; } = [];

    /// <summary>页面是否打开(盖在主工作区上;纯 UI 状态)。</summary>
    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial ScheduleTaskRowViewModel? SelectedTask { get; private set; }

    [ObservableProperty] public partial ScheduleRunRowViewModel? SelectedRun { get; private set; }

    [ObservableProperty] public partial ScheduledRunDto? RunDetail { get; private set; }

    [ObservableProperty] public partial bool RunsLoading { get; private set; }

    [ObservableProperty] public partial PendingConfirm? PendingDelete { get; private set; }

    public bool HasPendingDelete => PendingDelete is not null;

    partial void OnPendingDeleteChanged(PendingConfirm? value) => OnPropertyChanged(nameof(HasPendingDelete));

    public bool HasTasks => Tasks.Count > 0;

    public bool ShowsEmpty => IsReady && Tasks.Count == 0;

    public bool HasProject => Scope.WorkspaceId is not null;

    public bool HasMoreRuns => _historyHasMore;

    public bool HasRuns => Runs.Count > 0;

    [RelayCommand]
    private async Task OpenAsync()
    {
        IsOpen = true;
        await ActivateAsync();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private void FollowSelection()
    {
        // 选了别的会话 = 回到对话:定时任务页让位
        var session = _stores.Selection.State.SessionId;
        if (session != _lastSession)
        {
            _lastSession = session;
            IsOpen = false;
        }

        if (_stores.Selection.State.Project is { } project)
        {
            Scope.DeviceId = project.DeviceId;
            Scope.WorkspaceId = project.WorkspaceId;
        }

        OnPropertyChanged(nameof(HasProject));
    }

    protected override Task LoadAsync()
    {
        // 任务与运行经 schedules 主题全量推送(viewer 起);镜像到了就就绪
        Sync();
        return Task.CompletedTask;
    }

    protected override void OnUnavailable()
    {
        Tasks.Clear();
        Runs.Clear();
        Editor.IsOpen = false;
        PendingDelete = null;
    }

    private void Sync()
    {
        if (Scope.DeviceId is not { } device || !Scope.IsConnected)
        {
            return;
        }

        var mirror = _stores.Schedules.State.Of(device);
        if (!mirror.Known)
        {
            if (State == PageState.Ready)
            {
                State = PageState.Loading;
            }

            return;
        }

        var workspace = Scope.WorkspaceId;
        var rows = mirror.Tasks.Where(t => t.WorkspaceId == workspace)
            .OrderByDescending(t => t.Enabled).ThenBy(t => t.NextRunAt ?? long.MaxValue).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ScheduleTaskRowViewModel(this, t, mirror.IsRunning(t.Id),
                mirror.Runs.Where(r => r.TaskId == t.Id).OrderByDescending(r => r.ScheduledAt).FirstOrDefault())).ToList();
        if (!rows.Select(r => (r.Dto, r.IsRunning, r.LastRun)).SequenceEqual(Tasks.Select(r => (r.Dto, r.IsRunning, r.LastRun))))
        {
            Tasks.Clear();
            foreach (var row in rows)
            {
                Tasks.Add(row);
            }
        }

        if (SelectedTask is { } selected)
        {
            SelectedTask = Tasks.FirstOrDefault(t => t.Id == selected.Id);
            RebuildRuns();
        }

        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(HasProject));
        if (State != PageState.Ready)
        {
            Ready();
            OnPropertyChanged(nameof(ShowsEmpty));
        }

        SyncModels();
    }

    // ───────────────────────── 任务操作 ─────────────────────────

    internal async Task SetEnabledAsync(ScheduleTaskRowViewModel row, bool enabled)
    {
        if (Scope.DeviceId is { } device && CanWrite)
        {
            _ = await Act(_commands.SetEnabledAsync(device, row.Id, enabled));
        }
    }

    internal async Task RunNowAsync(ScheduleTaskRowViewModel row)
    {
        if (Scope.DeviceId is { } device && CanWrite && !row.IsRunning)
        {
            _ = await Act(_commands.RunNowAsync(device, row.Id));
        }
    }

    internal void AskDelete(ScheduleTaskRowViewModel row) => PendingDelete = CanWrite ? new PendingConfirm(row.Name, row.Id) : null;

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } pending || Scope.DeviceId is not { } device || !CanWrite)
        {
            return;
        }

        PendingDelete = null;
        var result = await Act(_commands.DeleteAsync(device, pending.Id));
        if (result.IsOk && SelectedTask?.Id == pending.Id)
        {
            SelectedTask = null;
            Runs.Clear();
        }
    }

    // ───────────────────────── 编辑器 ─────────────────────────

    private void SyncModels()
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        var options = _stores.Providers.State.Of(device).Options.Where(o => o.Enabled && o.ProviderEnabled)
            .Select(o => new ChoiceItem(GeneralSettingsViewModel.PickKey(new ModelPickDto(o.Alias, o.ProviderId)), $"{o.DisplayName} · {o.ProviderName}")).ToList();
        List<ChoiceItem> items = [new("", null), .. options];
        if (!items.SequenceEqual(Editor.ModelChoices))
        {
            Editor.ModelChoices.Clear();
            foreach (var i in items)
            {
                Editor.ModelChoices.Add(i);
            }
        }
    }

    [RelayCommand]
    private void New()
    {
        if (!CanWrite || Scope.WorkspaceId is null)
        {
            return;
        }

        var e = Editor;
        e.IsNew = true;
        e.TaskId = null;
        e.Name = "";
        e.Prompt = "";
        e.ModelKey = "";
        e.Kind = ScheduleRuleKinds.Daily;
        e.Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), TimeZoneInfo.Local).Date.AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        e.Time = "09:00";
        e.Timezone = ScheduleMath.SystemTimeZone();
        e.RepeatEnabled = false;
        e.RepeatEnd = "18:00";
        e.RepeatInterval = 60;
        e.Enabled = true;
        for (var d = 0; d < 7; d++)
        {
            e.Weekdays[d].IsOn = d is >= 1 and <= 5;
        }

        e.SaveFailure = null;
        e.IsOpen = true;
    }

    internal void Edit(ScheduleTaskRowViewModel row)
    {
        if (!CanWrite)
        {
            return;
        }

        var (e, t) = (Editor, row.Dto);
        e.IsNew = false;
        e.TaskId = t.Id;
        e.Name = t.Name;
        e.Prompt = t.Prompt;
        e.ModelKey = GeneralSettingsViewModel.PickKey(new ModelPickDto(t.Model, t.ModelProviderId));
        e.Kind = t.Schedule.Kind;
        var at = t.Schedule.At;
        e.Date = at is { Length: 16 } ? at[..10] : "";
        e.Time = t.Schedule.Kind == ScheduleRuleKinds.Once ? at is { Length: 16 } ? at[11..] : "09:00" : t.Schedule.Time ?? "09:00";
        e.Timezone = t.Timezone;
        e.RepeatEnabled = t.RepeatWindow.Enabled;
        e.RepeatEnd = t.RepeatWindow.EndTime ?? "18:00";
        e.RepeatInterval = t.RepeatWindow.IntervalMinutes ?? 60;
        e.Enabled = t.Enabled;
        foreach (var w in e.Weekdays)
        {
            w.IsOn = t.Schedule.Weekdays?.Contains(w.Day) == true;
        }

        e.SaveFailure = null;
        e.IsOpen = true;
    }

    [RelayCommand]
    private void CloseEditor() => Editor.IsOpen = false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Scope.DeviceId is not { } device || Scope.WorkspaceId is not { } workspace || !CanWrite || !Editor.CanSave)
        {
            return;
        }

        var e = Editor;
        var rule = new ScheduleRuleDto(e.Rule.Kind, e.Rule.At, e.Rule.Time, e.Rule.Weekdays?.ToArray());
        var repeat = new RepeatWindowDto(e.Repeat.Enabled, e.Repeat.EndTime, e.Repeat.IntervalMinutes);
        var pick = e.ModelKey.Length == 0 ? ModelPickDto.None : ParsePick(e.ModelKey);
        e.IsSaving = true;
        e.SaveFailure = null;
        try
        {
            var result = e.IsNew
                ? await _commands.CreateAsync(device, new ScheduledTaskInputDto(e.Name.Trim(), e.Prompt.Trim(), workspace, pick.Model, rule, pick.ProviderId, e.Timezone.Trim(), repeat, e.Enabled))
                : await _commands.UpdateAsync(device, e.TaskId!, new ScheduledTaskPatchDto(e.Name.Trim(), e.Prompt.Trim(), pick.Model, pick.ProviderId, rule, e.Timezone.Trim(), repeat, e.Enabled));
            if (result.IsOk)
            {
                e.IsOpen = false;
            }
            else
            {
                e.SaveFailure = Failure.Of(result);
            }
        }
        finally
        {
            e.IsSaving = false;
            e.Refresh();
        }
    }

    private static ModelPickDto ParsePick(string key)
    {
        var cut = key.IndexOf('\n', StringComparison.Ordinal);
        return new ModelPickDto(key[(cut + 1)..], cut > 0 ? key[..cut] : null);
    }

    // ───────────────────────── 运行记录 ─────────────────────────

    internal async Task SelectTaskAsync(ScheduleTaskRowViewModel row)
    {
        SelectedTask = row;
        SelectedRun = null;
        RunDetail = null;
        _history.Clear();
        _historyHasMore = false;
        RebuildRuns();
        await LoadHistoryAsync(row.Id, null, null);
    }

    [RelayCommand]
    private void CloseRuns()
    {
        SelectedTask = null;
        SelectedRun = null;
        RunDetail = null;
        Runs.Clear();
    }

    [RelayCommand]
    private async Task MoreRunsAsync()
    {
        if (SelectedTask is { } task && _history.Count > 0)
        {
            var last = _history.MinBy(r => (r.ScheduledAt, r.Id))!;
            await LoadHistoryAsync(task.Id, last.ScheduledAt, last.Id);
        }
    }

    private async Task LoadHistoryAsync(string taskId, long? beforeAt, string? beforeId)
    {
        if (Scope.DeviceId is not { } device)
        {
            return;
        }

        RunsLoading = true;
        var result = await _commands.ListRunsAsync(device, new ScheduledRunsQuery(taskId, 20, beforeAt, beforeId));
        RunsLoading = false;
        if (SelectedTask?.Id != taskId)
        {
            return; // 已经切到别的任务
        }

        if (!result.IsOk)
        {
            ActionFailure = Failure.Of(result);
            return;
        }

        _history.AddRange(result.Value!.Runs.Where(r => _history.All(h => h.Id != r.Id)));
        _historyHasMore = result.Value.HasMore;
        RebuildRuns();
    }

    /// <summary>显示的运行 = 主题里这个任务的运行(权威)∪ 翻页取回的更早记录;同一条以主题为准,但终态永远压过运行中(规则 6)。</summary>
    private void RebuildRuns()
    {
        if (SelectedTask is not { } task || Scope.DeviceId is not { } device)
        {
            return;
        }

        var live = _stores.Schedules.State.Of(device).Runs.Where(r => r.TaskId == task.Id).ToDictionary(r => r.Id);
        var merged = new Dictionary<string, ScheduledRunDto>(live);
        foreach (var old in _history)
        {
            if (!merged.TryGetValue(old.Id, out var current) || (ScheduleRunStatus.IsActive(current.Status) && ScheduleRunStatus.IsTerminal(old.Status)))
            {
                merged[old.Id] = old;
            }
        }

        var rows = merged.Values.OrderByDescending(r => r.ScheduledAt).ThenByDescending(r => r.Id, StringComparer.Ordinal).Select(r => new ScheduleRunRowViewModel(this, r)).ToList();
        if (!rows.Select(r => r.Dto).SequenceEqual(Runs.Select(r => r.Dto)))
        {
            Runs.Clear();
            foreach (var row in rows)
            {
                Runs.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasMoreRuns));
        OnPropertyChanged(nameof(HasRuns));
    }

    internal async Task SelectRunAsync(ScheduleRunRowViewModel row)
    {
        SelectedRun = row;
        RunDetail = row.Dto;
        if (Scope.DeviceId is { } device)
        {
            var result = await _commands.GetRunAsync(device, row.Id);
            if (result.IsOk && SelectedRun?.Id == row.Id)
            {
                RunDetail = result.Value;
            }
        }
    }

    internal async Task DeleteRunAsync(ScheduleRunRowViewModel row)
    {
        if (Scope.DeviceId is not { } device || !CanWrite || row.IsActive)
        {
            return;
        }

        var result = await Act(_commands.DeleteRunAsync(device, row.Id));
        if (result.IsOk)
        {
            _history.RemoveAll(r => r.Id == row.Id);
            if (SelectedRun?.Id == row.Id)
            {
                (SelectedRun, RunDetail) = (null, null);
            }

            RebuildRuns();
        }
    }
}
