using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>「新建项目」来源:本机目录或 SSH 主机上的远程目录(docs/design/03 §1、06 §7)。</summary>
public enum NewProjectSource
{
    Local,
    Ssh,
}

/// <summary>「新建项目」窗口为什么没能添加(界面按它出文字;<see cref="NewProjectViewModel.ErrorDetail"/> 是设备给的英文说明)。</summary>
public enum NewProjectError
{
    /// <summary>没填路径。</summary>
    PathRequired,

    /// <summary>选中的设备没连上。</summary>
    NotConnected,

    /// <summary>那台设备上没有这个文件夹。</summary>
    NotFound,

    /// <summary>本控制端在那台设备上的角色不能添加项目。</summary>
    Forbidden,

    /// <summary>其他失败。</summary>
    Failed,
}

/// <summary>「新建项目」里的一个可选 SSH 环境。</summary>
public sealed record SshEnvironmentChoice(string Id, string Name, string State);

/// <summary>
/// 「新建项目」窗口(docs/design/03 §1「栏 1」、06 §7):选一台已连接的设备,选择本机目录或 SSH 主机,
/// 填目录路径(本机可以系统浏览,SSH 支持远程目录浏览与面包屑导航),可选地起个名字。
/// 设备与环境列表只读 <see cref="ClientStores.Workspace"/> / <see cref="ClientStores.Environments"/>;提交走 <see cref="ProjectActions.AddAsync"/> 或 <see cref="IEnvironmentCommands.AddProjectAsync"/>。
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject, IDisposable
{
    private readonly ClientStores _stores;
    private readonly ProjectActions _actions;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IClientText> _text;
    private readonly IEnvironmentCommands? _environments;
    private readonly Action? _openEnvironments;
    private CancellationTokenSource? _browseCts;
    private CancellationTokenSource? _submitCts;
    private bool _disposed;

    public NewProjectViewModel(
        ClientStores stores,
        ProjectActions actions,
        Func<IDialogService> dialogs,
        Func<IClientText> text,
        string? preferredDeviceId = null,
        IEnvironmentCommands? environments = null,
        SshInteractionsViewModel? sshInteractions = null,
        Action? openEnvironments = null,
        NewProjectSource initialSource = NewProjectSource.Local)
    {
        _stores = stores;
        _actions = actions;
        _dialogs = dialogs;
        _text = text;
        _environments = environments;
        SshInteractions = sshInteractions;
        _openEnvironments = openEnvironments;
        Source = environments is not null ? initialSource : NewProjectSource.Local;
        RefreshDevices(preferredDeviceId);
        stores.Workspace.Changed += OnWorkspaceChanged;
        stores.Environments.Changed += OnEnvironmentsChanged;
        if (IsSshSource && EnvironmentId is not null)
        {
            _ = BrowseRemoteAsync(null);
        }
    }

    /// <summary>内联 SSH 指纹 / 凭据交互(远程浏览或添加项目时触发,直接显示在窗口内避免被对话框遮罩盖住)。</summary>
    public SshInteractionsViewModel? SshInteractions { get; }

    public bool SupportsSsh => _environments is not null;

    /// <summary>可以添加项目的设备:已连接、角色不是 viewer(<c>projects.add</c> 要 member 起)。本机在前。</summary>
    [ObservableProperty] public partial IReadOnlyList<DeviceChoice> Devices { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBrowse), nameof(IsRemoteDevice))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string? DeviceId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalSource), nameof(IsSshSource))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial NewProjectSource Source { get; set; }

    public bool IsLocalSource => Source == NewProjectSource.Local;

    public bool IsSshSource => Source == NewProjectSource.Ssh;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Path { get; set; } = "";

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial NewProjectError? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorKey))]
    public partial string? ErrorKey { get; private set; }

    [ObservableProperty] public partial string? ErrorDetail { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsBusy { get; private set; }

    // ── SSH 环境与远程目录浏览 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSshEnvironments))]
    public partial IReadOnlyList<SshEnvironmentChoice> SshEnvironments { get; private set; } = [];

    public bool HasSshEnvironments => SshEnvironments.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(BrowseRemoteHomeCommand), nameof(GoToRemotePathCommand), nameof(RefreshRemoteCommand))]
    public partial string? EnvironmentId { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(GoToRemotePathCommand), nameof(GoToParentCommand), nameof(RefreshRemoteCommand))]
    public partial bool IsBrowsing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirectory), nameof(CanGoUp), nameof(HasMultipleRoots), nameof(IsTruncated), nameof(HasEntries), nameof(ShowsEmptyDirectory))]
    public partial RemoteDirectoryDto? CurrentDirectory { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBrowseFailure))]
    public partial Failure? BrowseFailure { get; private set; }

    [ObservableProperty] public partial bool ShowHidden { get; set; }

    public ObservableCollection<RemoteDirEntryDto> Entries { get; } = [];

    public ObservableCollection<RemoteCrumbDto> Breadcrumbs { get; } = [];

    public ObservableCollection<string> Roots { get; } = [];

    public bool HasDirectory => CurrentDirectory is not null;

    public bool CanGoUp => !IsBrowsing && CurrentDirectory?.Parent is not null;

    public bool HasMultipleRoots => Roots.Count > 1;

    public bool IsTruncated => CurrentDirectory?.Truncated == true;

    public bool HasEntries => Entries.Count > 0;

    public bool ShowsEmptyDirectory => HasDirectory && !IsBrowsing && Entries.Count == 0;

    public bool HasBrowseFailure => BrowseFailure is not null;

    public bool CanOpenEnvironments => _openEnvironments is not null;

    public bool HasError => Error is not null || HasErrorKey;

    public bool HasErrorKey => !string.IsNullOrEmpty(ErrorKey);

    public bool HasDevices => Devices.Count > 0;

    private DeviceChoice? Selected => Devices.FirstOrDefault(d => d.DeviceId == DeviceId);

    /// <summary>选中的是本机:路径旁边有「浏览…」(系统文件夹选择框只能选这台电脑上的文件夹)。</summary>
    public bool CanBrowse => Selected?.Kind == DeviceKind.Local;

    /// <summary>选中的是另一台设备 / 云端:路径要手填(那台设备上的绝对路径)。</summary>
    public bool IsRemoteDevice => Selected is { Kind: not DeviceKind.Local };

    /// <summary>添加成功的项目(已选中)。</summary>
    public ProjectRef? Created { get; private set; }

    /// <summary>添加成功:界面据此关窗口。</summary>
    public event Action? Completed;

    /// <summary>用户点了「管理 SSH 服务器」:关掉弹窗并跳转到设置页。</summary>
    public event Action? ManageEnvironmentsRequested;

    /// <summary>出错时弹窗的标题(没有界面的回退路径用)。</summary>
    public string FailureTitle => _text().AddProjectFailedTitle;

    partial void OnDeviceIdChanged(string? value)
    {
        RefreshSshEnvironments();
        if (IsSshSource)
        {
            ResetRemoteBrowser();
            if (EnvironmentId is not null)
            {
                _ = BrowseRemoteAsync(null);
            }
        }
    }

    partial void OnSourceChanged(NewProjectSource value)
    {
        SetError(null, null, null);
        if (value == NewProjectSource.Ssh)
        {
            RefreshSshEnvironments();
            if (CurrentDirectory is null && EnvironmentId is not null)
            {
                _ = BrowseRemoteAsync(null);
            }
        }
        else
        {
            CancelBrowse();
        }
    }

    partial void OnEnvironmentIdChanged(string? value)
    {
        if (!IsSshSource)
        {
            return;
        }

        ResetRemoteBrowser();
        if (value is not null)
        {
            _ = BrowseRemoteAsync(null);
        }
    }

    partial void OnShowHiddenChanged(bool value) => RebuildEntries();

    private bool CanSubmit() => !IsBusy && DeviceId is not null && Selected is not null && (IsLocalSource || EnvironmentId is not null);

    [RelayCommand]
    private void SelectSource(NewProjectSource source)
    {
        if (source == NewProjectSource.Ssh && !SupportsSsh)
        {
            return;
        }

        Source = source;
    }

    [RelayCommand]
    private void ManageEnvironments()
    {
        ManageEnvironmentsRequested?.Invoke();
        _openEnvironments?.Invoke();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs().PickFolderAsync(_text().AddProjectPickTitle);
        if (!string.IsNullOrWhiteSpace(path))
        {
            Path = path;
            SetError(null, null, null);
        }
    }

    [RelayCommand]
    private Task BrowseRemoteHomeAsync() => BrowseRemoteAsync(null);

    [RelayCommand]
    private Task GoToRemotePathAsync() => BrowseRemoteAsync(string.IsNullOrWhiteSpace(Path) ? null : Path.Trim());

    [RelayCommand]
    private Task GoToParentAsync() => CurrentDirectory?.Parent is { } parent ? BrowseRemoteAsync(parent) : Task.CompletedTask;

    [RelayCommand]
    private Task NavigateToAsync(string? path) => string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : BrowseRemoteAsync(path);

    [RelayCommand]
    private Task RefreshRemoteAsync() => BrowseRemoteAsync(CurrentDirectory?.Path ?? (string.IsNullOrWhiteSpace(Path) ? null : Path.Trim()));

    [RelayCommand]
    private void CancelBrowse()
    {
        if (_browseCts is not null)
        {
            _browseCts.Cancel();
            _browseCts.Dispose();
            _browseCts = null;
        }
    }

    private async Task BrowseRemoteAsync(string? targetPath)
    {
        if (_environments is null || DeviceId is not { } deviceId || EnvironmentId is not { } envId || _disposed)
        {
            return;
        }

        CancelBrowse();
        var cts = new CancellationTokenSource();
        _browseCts = cts;
        IsBrowsing = true;
        BrowseFailure = null;
        SetError(null, null, null);
        try
        {
            var result = await _environments.BrowseAsync(deviceId, envId, targetPath, cts.Token);
            if (_disposed || !ReferenceEquals(_browseCts, cts))
            {
                return;
            }

            if (!result.IsOk)
            {
                if (!cts.IsCancellationRequested)
                {
                    BrowseFailure = Failure.Of(result);
                }

                return;
            }

            ApplyDirectory(result.Value!);
        }
        catch (OperationCanceledException)
        {
            // 用户取消浏览
        }
        finally
        {
            if (ReferenceEquals(_browseCts, cts))
            {
                _browseCts = null;
                IsBrowsing = false;
            }

            cts.Dispose();
        }
    }

    private void ApplyDirectory(RemoteDirectoryDto directory)
    {
        CurrentDirectory = directory;
        Path = directory.Path;
        Breadcrumbs.Clear();
        foreach (var crumb in directory.Breadcrumbs)
        {
            Breadcrumbs.Add(crumb);
        }

        Roots.Clear();
        foreach (var root in directory.Roots)
        {
            Roots.Add(root);
        }

        RebuildEntries();
        OnPropertyChanged(nameof(HasMultipleRoots));
        OnPropertyChanged(nameof(CanGoUp));
    }

    private void RebuildEntries()
    {
        Entries.Clear();
        if (CurrentDirectory is { } dir)
        {
            foreach (var entry in dir.Entries)
            {
                if (ShowHidden || !entry.Name.StartsWith('.'))
                {
                    Entries.Add(entry);
                }
            }
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(ShowsEmptyDirectory));
    }

    private void ResetRemoteBrowser()
    {
        CancelBrowse();
        CurrentDirectory = null;
        BrowseFailure = null;
        Path = "";
        Entries.Clear();
        Breadcrumbs.Clear();
        Roots.Clear();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(ShowsEmptyDirectory));
        OnPropertyChanged(nameof(HasMultipleRoots));
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitCoreAsync() => SubmitAsync();

    /// <summary>提交;成功返回 true 并触发 <see cref="Completed"/>,失败把原因放进 <see cref="Error"/>,窗口不关。</summary>
    public async Task<bool> SubmitAsync()
    {
        if (IsBusy || DeviceId is not { } deviceId)
        {
            return false;
        }

        var path = Path.Trim();
        if (path.Length == 0)
        {
            SetError(NewProjectError.PathRequired, null, null);
            return false;
        }

        IsBusy = true;
        SetError(null, null, null);
        _submitCts?.Cancel();
        _submitCts?.Dispose();
        var cts = new CancellationTokenSource();
        _submitCts = cts;
        try
        {
            if (IsSshSource)
            {
                if (_environments is null || EnvironmentId is not { } envId)
                {
                    SetError(NewProjectError.Failed, EnvironmentErrorKeys.NotFound, null);
                    return false;
                }

                var name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
                var sshResult = await _environments.AddProjectAsync(deviceId, envId, path, name, cts.Token);
                if (sshResult.IsOk)
                {
                    var project = new ProjectRef(deviceId, sshResult.Value!.WorkspaceId);
                    _stores.Selection.Update(_ => new SelectionState(project, null));
                    Created = project;
                    Completed?.Invoke();
                    return true;
                }

                SetError(MapOutcome(sshResult.Outcome), sshResult.ErrorKey, sshResult.Detail);
                return false;
            }

            var result = await _actions.AddAsync(deviceId, path, Name, cts.Token);
            if (result.IsOk)
            {
                Created = result.Value;
                Completed?.Invoke();
                return true;
            }

            SetError(MapOutcome(result.Outcome), result.ErrorKey, result.Detail);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_submitCts, cts))
            {
                _submitCts = null;
            }

            cts.Dispose();
            IsBusy = false;
        }
    }

    private static NewProjectError MapOutcome(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Unavailable => NewProjectError.NotConnected,
        ActionOutcome.NotFound => NewProjectError.NotFound,
        ActionOutcome.Forbidden => NewProjectError.Forbidden,
        _ => NewProjectError.Failed,
    };

    public IAsyncRelayCommand SubmitCommand => SubmitCoreCommand;

    public void Dispose()
    {
        _disposed = true;
        CancelBrowse();
        _submitCts?.Cancel();
        _submitCts?.Dispose();
        _submitCts = null;
        _stores.Workspace.Changed -= OnWorkspaceChanged;
        _stores.Environments.Changed -= OnEnvironmentsChanged;
    }

    private void SetError(NewProjectError? error, string? key, string? detail)
    {
        Error = error;
        ErrorKey = key;
        ErrorDetail = detail;
    }

    private void OnWorkspaceChanged() => RefreshDevices(DeviceId);

    private void OnEnvironmentsChanged() => RefreshSshEnvironments();

    private void RefreshDevices(string? preferred)
    {
        var choices = Choices(_stores.Workspace.State);
        if (!choices.SequenceEqual(Devices))
        {
            Devices = choices;
            OnPropertyChanged(nameof(HasDevices));
        }

        var next = choices.FirstOrDefault(c => c.DeviceId == preferred)
                   ?? choices.FirstOrDefault(c => c.Kind == DeviceKind.Local)
                   ?? choices.FirstOrDefault();
        if (next?.DeviceId != DeviceId)
        {
            DeviceId = next?.DeviceId;
        }
        else
        {
            OnPropertyChanged(nameof(CanBrowse));
            OnPropertyChanged(nameof(IsRemoteDevice));
            SubmitCoreCommand.NotifyCanExecuteChanged();
        }

        RefreshSshEnvironments();
    }

    private void RefreshSshEnvironments()
    {
        var list = DeviceId is { } deviceId
            ? _stores.Environments.State.Of(deviceId).Environments
                .Where(static e => e.Kind == EnvironmentKindNames.Ssh && e.Enabled)
                .OrderBy(static e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(static e => new SshEnvironmentChoice(e.Id, e.Name, e.State))
                .ToList()
            : [];
        if (!list.SequenceEqual(SshEnvironments))
        {
            SshEnvironments = list;
        }

        if (EnvironmentId is null || list.All(e => e.Id != EnvironmentId))
        {
            EnvironmentId = list.FirstOrDefault()?.Id;
        }
    }

    /// <summary>可以添加项目的设备(纯函数,单测用)。</summary>
    public static IReadOnlyList<DeviceChoice> Choices(WorkspaceState workspace) =>
    [
        .. workspace.Devices.Values
            .Where(d => d.Connection == DeviceConnection.Connected && d.Role != "viewer")
            .OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCulture).ThenBy(d => d.DeviceId, StringComparer.Ordinal)
            .Select(d => new DeviceChoice(d.DeviceId, d.Name, d.Kind, d.Connection)),
    ];
}
