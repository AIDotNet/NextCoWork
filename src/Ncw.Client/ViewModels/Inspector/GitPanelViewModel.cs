using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>提交历史里的一行。</summary>
public sealed record GitCommitItem(string Hash, string ShortHash, string Subject, string Author, DateTimeOffset At);

/// <summary>
/// Git 页签:变更列表(暂存 / 未暂存 / 未跟踪 / 冲突)、分支切换与新建、暂存与取消暂存、diff、提交(含「生成提交信息」)、拉取 / 推送、提交历史。
/// <b>事实只有一份</b>:仓库在设备上,控制端的镜像在 <see cref="ClientStores.Git"/>(<c>git.overview</c>;写操作成功后 <see cref="GitActions"/> 自己刷新它),
/// 这里的分组、分支名、领先 / 落后都是从那份镜像派生(分组规则是 <see cref="GitChangeGroups"/>,背后是 Core 的 <see cref="GitStatus"/>,不在视图里再写)。
/// 本类持有的只有纯 UI 状态:提交信息草稿、打开的 diff、分支列表、历史、忙碌标记、最近一次失败。设备断开时镜像是「未知」(<see cref="IsUnknown"/>),按钮停用,不把旧值当现状。
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IGitCommands _git;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IInspectorText> _text;
    private readonly Dictionary<ProjectRef, string> _drafts = [];
    private ProjectRef? _project;

    public GitPanelViewModel(ClientStores stores, IGitCommands git, Func<IDialogService> dialogs, Func<IInspectorText> text)
    {
        _stores = stores;
        _git = git;
        _dialogs = dialogs;
        _text = text;
        stores.Git.Changed += Derive;
    }

    public ProjectRef? Project => _project;

    public ObservableCollection<GitFileItem> Conflicted { get; } = [];

    public ObservableCollection<GitFileItem> Staged { get; } = [];

    public ObservableCollection<GitFileItem> Unstaged { get; } = [];

    public ObservableCollection<GitFileItem> Untracked { get; } = [];

    public ObservableCollection<GitBranchDto> Branches { get; } = [];

    public ObservableCollection<GitCommitItem> Commits { get; } = [];

    // ───────────────────────── 从镜像派生 ─────────────────────────

    /// <summary>仓库可用(已取到、有 git、有仓库)。</summary>
    [ObservableProperty] public partial bool IsAvailable { get; private set; }

    /// <summary>设备断开 / 还没取到:状态未知,界面不把旧值当现状(规则 7)。</summary>
    [ObservableProperty] public partial bool IsUnknown { get; private set; }

    /// <summary>不可用的原因(<see cref="GitUnavailableReasons"/>);可用或未知时为 null。</summary>
    [ObservableProperty] public partial string? UnavailableReason { get; private set; }

    [ObservableProperty] public partial string Branch { get; private set; } = "";

    [ObservableProperty] public partial bool IsDetached { get; private set; }

    [ObservableProperty] public partial string Upstream { get; private set; } = "";

    [ObservableProperty] public partial int Ahead { get; private set; }

    [ObservableProperty] public partial int Behind { get; private set; }

    [ObservableProperty] public partial GitStatusSummary Summary { get; private set; } = GitStatusSummary.Empty;

    [ObservableProperty] public partial bool FilesTruncated { get; private set; }

    public bool IsClean => IsAvailable && Summary.Clean;

    public bool HasConflicted => Conflicted.Count > 0;

    public bool HasStaged => Staged.Count > 0;

    public bool HasUnstaged => Unstaged.Count > 0;

    public bool HasUntracked => Untracked.Count > 0;

    public bool HasUpstream => Upstream.Length > 0;

    public bool HasAhead => Ahead > 0;

    public bool HasBehind => Behind > 0;

    // ───────────────────────── 纯 UI 状态 ─────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial string CommitMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit), nameof(CanOperate))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial bool IsGenerating { get; private set; }

    [ObservableProperty] public partial bool IsPulling { get; private set; }

    [ObservableProperty] public partial bool IsPushing { get; private set; }

    /// <summary>最近一次失败(下一次成功的操作清掉)。<c>Detail</c> 是 git 的英文诊断,放进「详情」。</summary>
    [ObservableProperty] public partial Problem? Failure { get; private set; }

    /// <summary>最近一次提交的短哈希(提交成功后给个确认)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastCommit))]
    public partial string? LastCommitShortHash { get; private set; }

    public bool HasLastCommit => LastCommitShortHash is not null;

    /// <summary>打开的 diff(点了变更列表里的一个文件);null = 显示列表。</summary>
    [ObservableProperty] public partial DiffViewModel? Diff { get; private set; }

    [ObservableProperty] public partial bool IsDiffLoading { get; private set; }

    [ObservableProperty] public partial GitFileItem? DiffItem { get; private set; }

    [ObservableProperty] public partial bool ShowHistory { get; private set; }

    [ObservableProperty] public partial bool IsHistoryLoading { get; private set; }

    public bool HasDiff => Diff is not null || IsDiffLoading;

    public bool ShowsChanges => !HasDiff && !ShowHistory;

    /// <summary>能操作:仓库可用、没有操作在进行、设备没断。</summary>
    public bool CanOperate => IsAvailable && !IsUnknown && !IsBusy;

    public bool CanCommit => CanOperate && Summary.Staged > 0 && CommitMessage.Trim().Length > 0;

    public bool CanGenerate => CanOperate && Summary.Staged > 0 && !IsGenerating;

    partial void OnDiffChanged(DiffViewModel? value)
    {
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(ShowsChanges));
    }

    partial void OnIsDiffLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(ShowsChanges));
    }

    partial void OnShowHistoryChanged(bool value) => OnPropertyChanged(nameof(ShowsChanges));

    partial void OnCommitMessageChanged(string value)
    {
        if (_project is { } project)
        {
            _drafts[project] = value;
        }

        OnPropertyChanged(nameof(CanGenerate));
    }

    partial void OnIsAvailableChanged(bool value) => NotifyCan();

    partial void OnIsUnknownChanged(bool value) => NotifyCan();

    partial void OnSummaryChanged(GitStatusSummary value)
    {
        NotifyCan();
        OnPropertyChanged(nameof(IsClean));
    }

    partial void OnIsGeneratingChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));

    partial void OnUpstreamChanged(string value) => OnPropertyChanged(nameof(HasUpstream));

    partial void OnAheadChanged(int value) => OnPropertyChanged(nameof(HasAhead));

    partial void OnBehindChanged(int value) => OnPropertyChanged(nameof(HasBehind));

    private void NotifyCan()
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanGenerate));
    }

    // ───────────────────────── 绑定与派生 ─────────────────────────

    /// <summary>换了项目:换提交信息草稿,关掉 diff 和历史。</summary>
    public void Bind(ProjectRef? project)
    {
        if (project == _project)
        {
            Derive();
            return;
        }

        _project = project;
        Diff = null;
        DiffItem = null;
        IsDiffLoading = false;
        ShowHistory = false;
        Commits.Clear();
        Branches.Clear();
        Failure = null;
        LastCommitShortHash = null;
        CommitMessage = project is { } p && _drafts.TryGetValue(p, out var draft) ? draft : "";
        Derive();
    }

    /// <summary>Store 变了之后重新派生(分组、分支、领先 / 落后全部来自 <see cref="ClientStores.Git"/>)。</summary>
    public void Derive()
    {
        var mirror = _project is { } project ? _stores.Git.State.Of(project) : ProjectGit.Unknown;
        var overview = mirror.Overview;
        IsUnknown = !mirror.Known;
        IsAvailable = mirror.Available;
        UnavailableReason = mirror.Known && overview is { Available: false } ? overview.Reason : null;
        Branch = overview?.Branch ?? "";
        IsDetached = overview?.Detached ?? false;
        Upstream = overview?.Upstream ?? "";
        Ahead = overview?.Ahead ?? 0;
        Behind = overview?.Behind ?? 0;
        FilesTruncated = overview?.FilesTruncated ?? false;
        Summary = mirror.Summary;

        var groups = GitChangeGroups.Of(overview);
        Sync(Conflicted, groups.Conflicted);
        Sync(Staged, groups.Staged);
        Sync(Unstaged, groups.Unstaged);
        Sync(Untracked, groups.Untracked);
        OnPropertyChanged(nameof(HasConflicted));
        OnPropertyChanged(nameof(HasStaged));
        OnPropertyChanged(nameof(HasUnstaged));
        OnPropertyChanged(nameof(HasUntracked));
    }

    private static void Sync(ObservableCollection<GitFileItem> target, IReadOnlyList<GitFileItem> source)
    {
        if (target.SequenceEqual(source))
        {
            return;
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    // ───────────────────────── 意图 ─────────────────────────

    /// <summary>拉取最新概览(打开页签、点刷新、换项目时)。</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        var result = await _git.RefreshAsync(project).ConfigureAwait(true);
        Failure = result.IsOk ? null : Problem.Of(result);
        if (result.IsOk)
        {
            await LoadBranchesAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task StageAsync(GitFileItem item) => RunAsync(project => _git.StageAsync(project, [item.Path]));

    [RelayCommand]
    private Task UnstageAsync(GitFileItem item) => RunAsync(project => _git.UnstageAsync(project, [item.Path]));

    /// <summary>暂存一个分组里的全部文件(未暂存 + 未跟踪)。</summary>
    [RelayCommand]
    private Task StageAllAsync() => RunAsync(project => _git.StageAsync(project, [.. Unstaged.Concat(Untracked).Select(i => i.Path).Distinct(StringComparer.Ordinal)]));

    [RelayCommand]
    private Task UnstageAllAsync() => RunAsync(project => _git.UnstageAsync(project, [.. Staged.Select(i => i.Path)]));

    /// <summary>打开一个文件的 diff。已暂存分组看暂存区的差异,其余看工作区的。</summary>
    [RelayCommand]
    private async Task OpenDiffAsync(GitFileItem item)
    {
        if (_project is not { } project)
        {
            return;
        }

        DiffItem = item;
        Diff = null;
        IsDiffLoading = true;
        Failure = null;
        try
        {
            var result = await _git.GetDiffAsync(project, item.Path, item.IsStaged).ConfigureAwait(true);
            if (DiffItem != item || _project != project)
            {
                return; // 用户又点了别的文件 / 换了项目
            }

            if (result.IsOk && result.Value is { } diff)
            {
                Diff = DiffViewModel.FromUnified(item.Path, diff.Text, diff.Binary, diff.Truncated);
            }
            else
            {
                Failure = Problem.Of(result);
                DiffItem = null;
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
        DiffItem = null;
        IsDiffLoading = false;
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (!CanCommit || _project is not { } project)
        {
            return;
        }

        var message = CommitMessage.Trim();
        var result = await RunAsync(p => _git.CommitAsync(p, message)).ConfigureAwait(true);
        if (result is { IsOk: true, Value: { } commit })
        {
            CommitMessage = "";
            LastCommitShortHash = commit.ShortHash;
            if (ShowHistory)
            {
                await LoadHistoryAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>用设置里的默认模型写一条提交信息草稿(填进输入框,不直接提交)。会花模型的钱。</summary>
    [RelayCommand]
    private async Task GenerateMessageAsync()
    {
        if (!CanGenerate || _project is not { } project)
        {
            return;
        }

        IsGenerating = true;
        try
        {
            var result = await _git.GenerateMessageAsync(project).ConfigureAwait(true);
            if (_project != project)
            {
                return;
            }

            if (result.IsOk && result.Value is { } generated)
            {
                CommitMessage = generated.Message;
                Failure = null;
            }
            else
            {
                Failure = Problem.Of(result);
            }
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        IsPulling = true;
        try
        {
            await RunAsync(project => _git.PullAsync(project)).ConfigureAwait(true);
        }
        finally
        {
            IsPulling = false;
        }
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        IsPushing = true;
        try
        {
            await RunAsync(project => _git.PushAsync(project)).ConfigureAwait(true);
        }
        finally
        {
            IsPushing = false;
        }
    }

    /// <summary>打开分支菜单时调:取最新的分支列表。</summary>
    [RelayCommand]
    private async Task LoadBranchesAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        var result = await _git.ListBranchesAsync(project).ConfigureAwait(true);
        if (_project != project)
        {
            return;
        }

        if (result.IsOk && result.Value is { } list)
        {
            Branches.Clear();
            foreach (var branch in list.Branches)
            {
                Branches.Add(branch);
            }
        }
        else
        {
            Failure = Problem.Of(result);
        }
    }

    [RelayCommand]
    private async Task CheckoutAsync(GitBranchDto branch)
    {
        if (branch.Current)
        {
            return;
        }

        if ((await RunAsync(project => _git.CheckoutAsync(project, branch.Name)).ConfigureAwait(true)).IsOk)
        {
            Diff = null;
            DiffItem = null;
        }
    }

    /// <summary>新建分支并切换过去(名字用弹窗问)。</summary>
    [RelayCommand]
    private async Task CreateBranchAsync()
    {
        var text = _text();
        var name = await _dialogs().PromptTextAsync(text.NewBranchTitle, text.NewBranchDescription, text.BranchNameLabel, null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(project => _git.CreateBranchAsync(project, name.Trim(), checkout: true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleHistoryAsync()
    {
        ShowHistory = !ShowHistory;
        if (ShowHistory)
        {
            await LoadHistoryAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadHistoryAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        IsHistoryLoading = true;
        try
        {
            var result = await _git.ListCommitsAsync(project, 50).ConfigureAwait(true);
            if (_project != project)
            {
                return;
            }

            if (result.IsOk && result.Value is { } list)
            {
                Commits.Clear();
                foreach (var commit in list.Commits)
                {
                    Commits.Add(new GitCommitItem(commit.Hash, commit.ShortHash, commit.Subject, commit.Author, DateTimeOffset.FromUnixTimeMilliseconds(commit.Timestamp)));
                }
            }
            else
            {
                Failure = Problem.Of(result);
            }
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    /// <summary>
    /// 一个写操作:忙碌标记 + 调用 + 记录失败。成功后 <see cref="GitActions"/> 已经刷新了概览(镜像变了,<see cref="Derive"/> 自然跟上),这里不再自己拉。
    /// </summary>
    private async Task<ActionResult<T>> RunAsync<T>(Func<ProjectRef, Task<ActionResult<T>>> call)
    {
        if (_project is not { } project || IsBusy)
        {
            return new ActionResult<T>(ActionOutcome.Unavailable);
        }

        IsBusy = true;
        try
        {
            var result = await call(project).ConfigureAwait(true);
            Failure = result.IsOk ? null : Problem.Of(result);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
