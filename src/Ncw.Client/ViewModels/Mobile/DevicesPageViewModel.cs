using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>设备页的一行:一台设备(本机、云端、配对过的电脑)和它此刻的状态。数据只来自 Store 镜像。</summary>
/// <param name="Address">配对过的远程设备的 <c>主机:端口</c>;本机和云端没有。</param>
/// <param name="Role">本控制端在这台设备上的角色(连上之后才知道)。</param>
/// <param name="Projects">这台设备上的项目数。</param>
/// <param name="Pending">这台设备上待处理的交互数(来自权威广播;断开时未知,界面不显示数字)。</param>
/// <param name="Unknown">设备没连上:运行 / 待处理的状态未知。</param>
public sealed record DeviceRow(string DeviceId, string Name, DeviceKind Kind, DeviceConnection Connection, string? Address, string? Role, int Projects, int Running, int Pending, bool Unknown)
{
    /// <summary>可以「移除」:只有配对过的远程设备(本机 / 云端不由这里管)。</summary>
    public bool CanRemove => Kind == DeviceKind.Remote;

    public bool IsConnected => Connection == DeviceConnection.Connected;

    /// <summary>有数字可显示(断开时运行 / 待处理数是未知,不显示)。</summary>
    public bool HasRunning => !Unknown && Running > 0;

    public bool HasPending => !Unknown && Pending > 0;
}

/// <summary>
/// 手机壳「设备」标签的根页:所有设备的列表和状态,以及「添加设备」的两个入口(扫码、IP:端口 + 配对码)。
/// 添加设备的流程各有自己的页面和状态机(<see cref="QrPairingViewModel"/>、<see cref="DevicesSettingsViewModel"/>),这里只负责列表和导航意图。
/// </summary>
public sealed partial class DevicesPageViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly DeviceActions _actions;
    private readonly MobileNavigationViewModel _navigation;

    public DevicesPageViewModel(ClientStores stores, DeviceActions actions, MobileNavigationViewModel navigation)
    {
        _stores = stores;
        _actions = actions;
        _navigation = navigation;
        stores.Workspace.Changed += Refresh;
        stores.Paired.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<DeviceRow> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    [RelayCommand]
    private void AddByQr() => _navigation.OpenDevicesPage(new QrPairRoute());

    [RelayCommand]
    private void AddManually() => _navigation.OpenDevicesPage(new ManualAddRoute());

    [RelayCommand]
    private Task RemoveAsync(string deviceId) => _actions.RemoveAsync(deviceId);

    private void Refresh()
    {
        var workspace = _stores.Workspace.State;
        var paired = _stores.Paired.State.ToDictionary(static d => d.DeviceId);
        var rows = workspace.Devices.Values
            .Select(d =>
            {
                var unknown = d.Connection != DeviceConnection.Connected;
                var running = d.Index?.Runs.Length ?? 0;
                var pending = d.Index?.Runs.Sum(static r => r.PendingInteractions) ?? 0;
                return new DeviceRow(
                    d.DeviceId, d.Name, d.Kind, d.Connection,
                    paired.TryGetValue(d.DeviceId, out var saved) ? $"{saved.Host}:{saved.Port}" : null,
                    d.Role, workspace.Projects.Keys.Count(p => p.DeviceId == d.DeviceId), running, pending, unknown);
            })
            // 本机、云端在前,其余按名字
            .OrderBy(static r => r.Kind == DeviceKind.Local ? 0 : r.Kind == DeviceKind.Cloud ? 1 : 2)
            .ThenBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static r => r.DeviceId, StringComparer.Ordinal)
            .ToList();

        if (rows.SequenceEqual(Items))
        {
            return;
        }

        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(nameof(HasItems));
    }
}
