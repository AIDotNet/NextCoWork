using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>改动审查里的一轮(一个顶层 Run 改过文件)。</summary>
public sealed record ReviewRunItem(string RunId, bool IsReverted, int FileCount, int Additions, int Deletions, DateTimeOffset UpdatedAt)
{
    public bool IsApplied => !IsReverted;

    public bool HasStats => Additions > 0 || Deletions > 0;
}

/// <summary>变更集里的一个文件。</summary>
public sealed record ReviewFileItem(string Path, string ChangeKind, int Additions, int Deletions, bool Oversize, bool InWorkspace)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Directory => Path.LastIndexOf('/') is > 0 and var i ? Path[..i] : "";

    public bool IsCreated => ChangeKind == ReviewChangeKinds.Created;

    public bool IsDeleted => ChangeKind == ReviewChangeKinds.Deleted;

    public bool IsModified => !IsCreated && !IsDeleted;

    public string Letter => IsCreated ? "A" : IsDeleted ? "D" : "M";

    public bool HasStats => Additions > 0 || Deletions > 0;
}

/// <summary>撤销 / 重做的结局(逐文件)。视图按语言写文字。</summary>
public sealed record ReviewOutcome(bool IsUndo, int Ok, int Conflict, int Skipped, int Error)
{
    public bool IsAllOk => Conflict == 0 && Skipped == 0 && Error == 0;

    public static ReviewOutcome Of(bool undo, ReviewMutationDto mutation) => new(
        undo,
        mutation.Files.Count(f => f.Status == ReviewFileStatuses.Ok),
        mutation.Files.Count(f => f.Status == ReviewFileStatuses.Conflict),
        mutation.Files.Count(f => f.Status == ReviewFileStatuses.Skipped),
        mutation.Files.Count(f => f.Status == ReviewFileStatuses.Error));
}

/// <summary>
/// 改动审查页签:当前会话里改过文件的各轮(最新在前)、选中一轮的文件列表、文件 diff(before / after 全文,行模型是 <see cref="UnifiedDiff"/>)、撤销 / 重做。
/// 变更集摘要的镜像在 <see cref="ClientStores.Review"/>(设备库是真源;断开时 <c>Known</c> = false,按钮停用,不把旧值当现状),这里只派生;
/// 全文 diff 是纯 UI 状态(不进 Store)。撤销前先 <c>precheck</c>:磁盘上又被改过的文件列出来让用户确认,确认后带 <c>force</c> 重发。
/// </summary>
public sealed partial class ReviewPanelViewModel : ObservableObject
{
    private readonly ClientStores stores;
    private readonly IReviewCommands review;
    private string? _deviceId;
    private string? _sessionId;
    private string? _selectedRunId;
    private bool _userPicked;

    public ReviewPanelViewModel(ClientStores stores, IReviewCommands review)
    {
        this.stores = stores;
        this.review = review;
        stores.Review.Changed += Derive;
    }

    public ObservableCollection<ReviewRunItem> Runs { get; } = [];

    public ObservableCollection<ReviewFileItem> Files { get; } = [];

    public ObservableCollection<string> PendingConflicts { get; } = [];

    [ObservableProperty] public partial ReviewRunItem? Selected { get; private set; }

    [ObservableProperty] public partial bool IsUnknown { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    [ObservableProperty] public partial DiffViewModel? Diff { get; private set; }

    [ObservableProperty] public partial ReviewFileItem? DiffFile { get; private set; }

    [ObservableProperty] public partial bool IsDiffLoading { get; private set; }

    [ObservableProperty] public partial ReviewOutcome? LastOutcome { get; private set; }

    [ObservableProperty] public partial Problem? Failure { get; private set; }

    public bool HasRuns => Runs.Count > 0;

    public bool IsEmpty => !IsLoading && Runs.Count == 0;

    public bool HasDiff => Diff is not null || IsDiffLoading;

    public bool ShowsList => !HasDiff;

    public bool HasPendingConflicts => PendingConflicts.Count > 0;

    public bool CanUndo => Selected is { IsApplied: true } && !IsBusy && !IsUnknown;

    public bool CanRedo => Selected is { IsReverted: true } && !IsBusy && !IsUnknown;

    partial void OnSelectedChanged(ReviewRunItem? value) => NotifyCan();

    partial void OnIsBusyChanged(bool value) => NotifyCan();

    partial void OnIsUnknownChanged(bool value) => NotifyCan();

    partial void OnDiffChanged(DiffViewModel? value) => NotifyDiff();

    partial void OnIsDiffLoadingChanged(bool value) => NotifyDiff();

    private void NotifyCan()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void NotifyDiff()
    {
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(ShowsList));
    }

    /// <summary>换了会话(或设备):回到最新一轮,关掉 diff。</summary>
    public void Bind(string? deviceId, string? sessionId)
    {
        if (deviceId == _deviceId && sessionId == _sessionId)
        {
            Derive();
            return;
        }

        _deviceId = deviceId;
        _sessionId = sessionId;
        _selectedRunId = null;
        _userPicked = false;
        Diff = null;
        DiffFile = null;
        LastOutcome = null;
        Failure = null;
        PendingConflicts.Clear();
        OnPropertyChanged(nameof(HasPendingConflicts));
        Derive();
    }

    /// <summary>Store 变了之后重新派生:各轮、选中轮的文件、未知状态。</summary>
    public void Derive()
    {
        if (_deviceId is not { } deviceId || _sessionId is not { } sessionId)
        {
            Runs.Clear();
            Files.Clear();
            Selected = null;
            IsUnknown = false;
            Raise();
            return;
        }

        var mirror = stores.Review.State;
        var mine = mirror.Runs
            .Where(kv => kv.Key.DeviceId == deviceId && kv.Value.ChangeSet is { } set && set.SessionId == sessionId)
            .Select(kv => (Run: kv.Value, Set: kv.Value.ChangeSet!))
            .OrderByDescending(x => x.Set.UpdatedAt)
            .ThenByDescending(x => x.Set.RunId, StringComparer.Ordinal)
            .ToList();

        var items = mine.Select(x => new ReviewRunItem(x.Set.RunId, x.Set.State == ReviewStates.Reverted, x.Set.FileCount, x.Set.Additions, x.Set.Deletions, DateTimeOffset.FromUnixTimeMilliseconds(x.Set.UpdatedAt))).ToList();
        if (!Runs.SequenceEqual(items))
        {
            Runs.Clear();
            foreach (var item in items)
            {
                Runs.Add(item);
            }
        }

        // 默认选最新一轮;用户点过哪一轮就一直是那一轮(除非它不在了)
        var picked = _userPicked && items.FirstOrDefault(i => i.RunId == _selectedRunId) is { } keep ? keep : items.FirstOrDefault();
        _selectedRunId = picked?.RunId;
        Selected = picked;
        var selectedSet = mine.FirstOrDefault(x => x.Set.RunId == picked?.RunId).Set;
        var files = selectedSet?.Files.Select(f => new ReviewFileItem(f.Path, f.ChangeKind, f.Additions, f.Deletions, f.Oversize, f.InWorkspace)).ToList() ?? [];
        if (!Files.SequenceEqual(files))
        {
            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(file);
            }
        }

        // 这个会话的任何一个变更集没有确认过 = 未知(设备断了)
        IsUnknown = mine.Count > 0 && mine.Any(x => !x.Run.Known);
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyCan();
    }

    /// <summary>取这个会话里改过文件的各轮(打开页签、会话切换、点刷新);结果由 Action 合进 Store,界面经 <see cref="Derive"/> 跟上。</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_deviceId is not { } deviceId || _sessionId is not { } sessionId)
        {
            return;
        }

        IsLoading = true;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var result = await review.ListAsync(deviceId, sessionId).ConfigureAwait(true);
            Failure = result.IsOk ? null : Problem.Of(result);
        }
        finally
        {
            IsLoading = false;
            Derive();
        }
    }

    [RelayCommand]
    private void SelectRun(ReviewRunItem run)
    {
        _userPicked = true;
        _selectedRunId = run.RunId;
        Diff = null;
        DiffFile = null;
        LastOutcome = null;
        PendingConflicts.Clear();
        OnPropertyChanged(nameof(HasPendingConflicts));
        Derive();
    }

    [RelayCommand]
    private async Task OpenFileAsync(ReviewFileItem file)
    {
        if (_deviceId is not { } deviceId || Selected is not { } run)
        {
            return;
        }

        DiffFile = file;
        Diff = null;
        IsDiffLoading = true;
        Failure = null;
        try
        {
            var result = await review.FileDiffAsync(deviceId, run.RunId, file.Path).ConfigureAwait(true);
            if (DiffFile != file)
            {
                return;
            }

            if (result.IsOk && result.Value is { } diff)
            {
                Diff = DiffViewModel.FromSides(file.Path, diff.Before, diff.After, diff.Oversize);
            }
            else if (result.IsOk)
            {
                // 设备上没有这个文件的记录了
                Failure = new Problem(ActionOutcome.NotFound, null);
                DiffFile = null;
            }
            else
            {
                Failure = Problem.Of(result);
                DiffFile = null;
            }
        }
        finally
        {
            IsDiffLoading = false;
        }
    }

    [RelayCommand]
    private void CloseDiff()
    {
        Diff = null;
        DiffFile = null;
        IsDiffLoading = false;
    }

    /// <summary>撤销整轮:先预检。有冲突(磁盘上已经又被改过的文件)就列出来等用户确认(<see cref="ForceUndoCommand"/>),没有就直接撤销。</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!CanUndo || _deviceId is not { } deviceId || Selected is not { } run)
        {
            return;
        }

        IsBusy = true;
        try
        {
            LastOutcome = null;
            var check = await review.PrecheckUndoAsync(deviceId, run.RunId).ConfigureAwait(true);
            if (!check.IsOk)
            {
                Failure = Problem.Of(check);
                return;
            }

            if (check.Value is { Length: > 0 } conflicts)
            {
                PendingConflicts.Clear();
                foreach (var path in conflicts)
                {
                    PendingConflicts.Add(path);
                }

                OnPropertyChanged(nameof(HasPendingConflicts));
                return;
            }

            await ApplyUndoAsync(deviceId, run.RunId, force: false).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>用户看过冲突列表后确认:带 force 撤销(冲突的文件也还原)。</summary>
    [RelayCommand]
    private async Task ForceUndoAsync()
    {
        if (_deviceId is not { } deviceId || Selected is not { } run || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            PendingConflicts.Clear();
            OnPropertyChanged(nameof(HasPendingConflicts));
            await ApplyUndoAsync(deviceId, run.RunId, force: true).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelUndo()
    {
        PendingConflicts.Clear();
        OnPropertyChanged(nameof(HasPendingConflicts));
    }

    private async Task ApplyUndoAsync(string deviceId, string runId, bool force)
    {
        var result = await review.UndoAsync(deviceId, runId, force).ConfigureAwait(true);
        if (result.IsOk && result.Value is { } mutation)
        {
            LastOutcome = ReviewOutcome.Of(undo: true, mutation);
            Failure = null;
        }
        else
        {
            Failure = Problem.Of(result);
        }

        Derive();
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        if (!CanRedo || _deviceId is not { } deviceId || Selected is not { } run)
        {
            return;
        }

        IsBusy = true;
        try
        {
            LastOutcome = null;
            var result = await review.RedoAsync(deviceId, run.RunId).ConfigureAwait(true);
            if (result.IsOk && result.Value is { } mutation)
            {
                LastOutcome = ReviewOutcome.Of(undo: false, mutation);
                Failure = null;
            }
            else
            {
                Failure = Problem.Of(result);
            }

            Derive();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
