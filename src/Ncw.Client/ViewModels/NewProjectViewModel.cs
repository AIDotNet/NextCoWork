using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

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

/// <summary>
/// 「新建项目」窗口(docs/design/03 §1「栏 1」):选一台已连接的设备,填那台设备上的文件夹路径(本机设备可以浏览),可选地起个名字。
/// 设备列表只读 <see cref="ClientStores.Workspace"/>(打开时取一次,窗口开着时设备上下线会跟着刷新);提交走 <see cref="ProjectActions.AddAsync"/>。
/// 路径、名字、错误都是这个窗口自己的纯 UI 状态,关掉就丢。
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject, IDisposable
{
    private readonly ClientStores _stores;
    private readonly ProjectActions _actions;
    private readonly Func<IDialogService> _dialogs;
    private readonly Func<IClientText> _text;

    public NewProjectViewModel(ClientStores stores, ProjectActions actions, Func<IDialogService> dialogs, Func<IClientText> text, string? preferredDeviceId = null)
    {
        _stores = stores;
        _actions = actions;
        _dialogs = dialogs;
        _text = text;
        RefreshDevices(preferredDeviceId);
        stores.Workspace.Changed += OnWorkspaceChanged;
    }

    /// <summary>可以添加项目的设备:已连接、角色不是 viewer(<c>projects.add</c> 要 member 起)。本机在前。</summary>
    [ObservableProperty] public partial IReadOnlyList<DeviceChoice> Devices { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBrowse), nameof(IsRemoteDevice))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string? DeviceId { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Path { get; set; } = "";

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial NewProjectError? Error { get; private set; }

    [ObservableProperty] public partial string? ErrorDetail { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsBusy { get; private set; }

    public bool HasError => Error is not null;

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

    /// <summary>出错时弹窗的标题(没有界面的回退路径用)。</summary>
    public string FailureTitle => _text().AddProjectFailedTitle;

    private bool CanSubmit() => !IsBusy && DeviceId is not null && Selected is not null;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs().PickFolderAsync(_text().AddProjectPickTitle);
        if (!string.IsNullOrWhiteSpace(path))
        {
            Path = path;
            Error = null;
        }
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
            SetError(NewProjectError.PathRequired, null);
            return false;
        }

        IsBusy = true;
        SetError(null, null);
        try
        {
            var result = await _actions.AddAsync(deviceId, path, Name);
            if (result.IsOk)
            {
                Created = result.Value;
                Completed?.Invoke();
                return true;
            }

            SetError(result.Outcome switch
            {
                ActionOutcome.Unavailable => NewProjectError.NotConnected,
                ActionOutcome.NotFound => NewProjectError.NotFound,
                ActionOutcome.Forbidden => NewProjectError.Forbidden,
                _ => NewProjectError.Failed,
            }, result.Detail);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public IAsyncRelayCommand SubmitCommand => SubmitCoreCommand;

    public void Dispose() => _stores.Workspace.Changed -= OnWorkspaceChanged;

    private void SetError(NewProjectError? error, string? detail)
    {
        Error = error;
        ErrorDetail = detail;
    }

    private void OnWorkspaceChanged() => RefreshDevices(DeviceId);

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
            // 同一台设备的类型 / 连接变了也要刷新「浏览」是否可用
            OnPropertyChanged(nameof(CanBrowse));
            OnPropertyChanged(nameof(IsRemoteDevice));
            SubmitCoreCommand.NotifyCanExecuteChanged();
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
