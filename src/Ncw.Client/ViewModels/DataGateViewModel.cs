using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

/// <summary>接管页显示什么。</summary>
public enum DataGateMode
{
    Hidden,
    Preparing,
    TakingOver,
    Blocked,
    Failed,

    /// <summary>刚完成 2.x 接管:显示迁移了多少项目和会话,用户点「继续」后关闭。</summary>
    Completed,
}

/// <summary>
/// 启动时的数据页(docs/design/04 §4、07 §1):本机设备的数据没就绪时盖住整个窗口,显示接管进度、被挡住的原因和「重试」。
/// 只读 Store 里本机设备的 <see cref="DeviceInfo.Data"/>;文字由视图按 <see cref="Step"/> / <see cref="Reason"/> 生成。
/// </summary>
public sealed partial class DataGateViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private readonly DataActions _actions;
    private bool _summaryDismissed;

    public DataGateViewModel(ClientStores stores, DeviceRegistry devices, DataActions actions)
    {
        _stores = stores;
        _devices = devices;
        _actions = actions;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    [ObservableProperty] public partial DataGateMode Mode { get; private set; }

    /// <summary>接管进行中的步骤(<see cref="DataSteps"/>)。</summary>
    [ObservableProperty] public partial string? Step { get; private set; }

    /// <summary>被挡住或失败的原因(<see cref="DataReasons"/>)。</summary>
    [ObservableProperty] public partial string? Reason { get; private set; }

    [ObservableProperty] public partial TakeoverSummary? Summary { get; private set; }

    [ObservableProperty] public partial bool IsRetrying { get; private set; }

    /// <summary>本机设备的数据已就绪(首次启动流程的「数据」一步据此显示完成)。没连上为 false。</summary>
    [ObservableProperty] public partial bool IsReady { get; private set; }

    public bool IsVisible => Mode != DataGateMode.Hidden;

    public bool CanRetry => Mode is DataGateMode.Blocked or DataGateMode.Failed;

    /// <summary>正在准备或接管(显示转圈)。</summary>
    public bool IsBusy => Mode is DataGateMode.Preparing or DataGateMode.TakingOver;

    public bool IsCompleted => Mode == DataGateMode.Completed;

    partial void OnModeChanged(DataGateMode value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsCompleted));
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        IsRetrying = true;
        try
        {
            await _actions.RetryLocalAsync();
        }
        finally
        {
            IsRetrying = false;
        }
    }

    [RelayCommand]
    private void Continue()
    {
        _summaryDismissed = true;
        Refresh();
    }

    private void Refresh()
    {
        var data = _devices.Local is { } link && _stores.Workspace.State.Devices.TryGetValue(link.DeviceId, out var device) ? device.Data : null;
        Step = data?.Step;
        Reason = data?.Reason;
        Summary = data?.TakenOver;
        IsReady = data?.State == DataStates.Ready;
        Mode = data?.State switch
        {
            null => DataGateMode.Hidden, // 没连上:连接状态在别处显示
            DataStates.Preparing => DataGateMode.Preparing,
            DataStates.TakingOver => DataGateMode.TakingOver,
            DataStates.Blocked => DataGateMode.Blocked,
            DataStates.Failed => DataGateMode.Failed,
            DataStates.Ready when data.TakenOver is not null && !_summaryDismissed => DataGateMode.Completed,
            _ => DataGateMode.Hidden,
        };
    }
}
