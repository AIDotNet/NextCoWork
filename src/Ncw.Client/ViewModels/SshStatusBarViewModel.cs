using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 当前选中 SSH 项目的连接状态条(对齐 2.x <c>ConnectionDialogs</c> 顶部状态栏,docs/design/06 §3 / §7):
/// 当项目所在 SSH 环境处于连接中、重连中、失败或设备断开(未知)时显示,提供重连、取消和前往设置(忘记指纹)的入口。
/// 处于 <c>idle</c>(惰性连接尚未激活)或 <c>ready</c>(正常)时不打扰用户。
/// </summary>
public sealed partial class SshStatusBarViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IEnvironmentCommands _commands;
    private readonly Action _openEnvironments;
    private CancellationTokenSource? _connectCts;

    public SshStatusBarViewModel(ClientStores stores, IEnvironmentCommands commands, Action openEnvironments)
    {
        _stores = stores;
        _commands = commands;
        _openEnvironments = openEnvironments;
        stores.Selection.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        stores.Environments.Changed += Refresh;
        Refresh();
    }

    [ObservableProperty] public partial bool IsVisible { get; private set; }

    [ObservableProperty] public partial string? DeviceId { get; private set; }

    [ObservableProperty] public partial string? EnvironmentId { get; private set; }

    [ObservableProperty] public partial string EnvironmentName { get; private set; } = "";

    [ObservableProperty] public partial string RemotePath { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting), nameof(IsFailed), nameof(IsUnknown), nameof(CanReconnect), nameof(CanCancel))]
    public partial EnvironmentDisplayState State { get; private set; } = EnvironmentDisplayState.Idle;

    [ObservableProperty] public partial string? ErrorKey { get; private set; }

    [ObservableProperty] public partial HostKeyMismatchDto? HostKeyMismatch { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReconnect), nameof(CanCancel))]
    public partial bool IsReconnectingLocally { get; private set; }

    public bool IsConnecting => State is EnvironmentDisplayState.Connecting or EnvironmentDisplayState.Reconnecting || IsReconnectingLocally;

    public bool IsFailed => State == EnvironmentDisplayState.Failed && !IsReconnectingLocally;

    public bool IsUnknown => State == EnvironmentDisplayState.Unknown;

    public bool HasHostKeyMismatch => HostKeyMismatch is not null;

    public bool HasErrorKey => !string.IsNullOrEmpty(ErrorKey);

    public bool CanReconnect => IsFailed && DeviceId is not null && EnvironmentId is not null;

    public bool CanCancel => IsReconnectingLocally && _connectCts is not null;

    partial void OnHostKeyMismatchChanged(HostKeyMismatchDto? value) => OnPropertyChanged(nameof(HasHostKeyMismatch));

    partial void OnErrorKeyChanged(string? value) => OnPropertyChanged(nameof(HasErrorKey));

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (DeviceId is not { } deviceId || EnvironmentId is not { } envId || IsReconnectingLocally)
        {
            return;
        }

        CancelConnect();
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        IsReconnectingLocally = true;
        try
        {
            var result = await _commands.ConnectAsync(deviceId, envId, cts.Token);
            if (!result.IsOk && !cts.IsCancellationRequested)
            {
                ErrorKey = result.ErrorKey;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消重连
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }

            cts.Dispose();
            IsReconnectingLocally = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void CancelConnect()
    {
        if (_connectCts is not null)
        {
            _connectCts.Cancel();
            _connectCts.Dispose();
            _connectCts = null;
        }
    }

    [RelayCommand]
    private void OpenSettings() => _openEnvironments();

    private void Refresh()
    {
        var selection = _stores.Selection.State;
        var workspace = _stores.Workspace.State;
        if (selection.Project is not { } project
            || !workspace.Projects.TryGetValue(project, out var info)
            || info.Environment != EnvironmentKind.Ssh)
        {
            IsVisible = false;
            DeviceId = null;
            EnvironmentId = null;
            State = EnvironmentDisplayState.Idle;
            ErrorKey = null;
            HostKeyMismatch = null;
            return;
        }

        DeviceId = project.DeviceId;
        EnvironmentId = info.EnvironmentId;
        EnvironmentName = info.EnvironmentName ?? info.EnvironmentId ?? "SSH";
        RemotePath = info.Path;

        var deviceConnected = workspace.Devices.TryGetValue(project.DeviceId, out var device) && device.Connection == DeviceConnection.Connected;
        var mirror = _stores.Environments.State.Of(project.DeviceId);
        var dto = info.EnvironmentId is { } id ? mirror.Find(id) : null;

        if (!deviceConnected)
        {
            State = EnvironmentDisplayState.Unknown;
            ErrorKey = null;
            HostKeyMismatch = null;
            IsVisible = true;
            return;
        }

        if (!mirror.Known || dto is null)
        {
            State = EnvironmentDisplayState.Idle;
            ErrorKey = null;
            HostKeyMismatch = null;
            IsVisible = IsReconnectingLocally;
            return;
        }

        State = EnvironmentStates.Of(true, dto.State);
        ErrorKey = dto.State == EnvironmentStateNames.Failed ? dto.Error?.Key : null;
        HostKeyMismatch = dto.State == EnvironmentStateNames.Failed ? dto.Error?.HostKey : null;
        IsVisible = State is EnvironmentDisplayState.Connecting or EnvironmentDisplayState.Reconnecting or EnvironmentDisplayState.Failed || IsReconnectingLocally;
    }
}
