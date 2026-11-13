using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>设置页的分组(docs/design/07)。先实现「设备」和「连接与远程」,其余分组随功能加入。</summary>
public enum SettingsSection
{
    /// <summary>本应用:本控制端配对过的设备、添加设备。</summary>
    Devices,

    /// <summary>设备(本机):远程开关、配对码、已配对的控制端。</summary>
    Remote,
}

/// <summary>设置页:整窗覆盖在外壳上,左侧导航 + 内容区。打开与否、当前分组是纯 UI 状态。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ClientStores stores, DeviceRegistry devices, DeviceActions actions)
    {
        Devices = new DevicesSettingsViewModel(stores, devices, actions);
        Remote = new RemoteSettingsViewModel(stores, devices, actions);
    }

    public DevicesSettingsViewModel Devices { get; }

    public RemoteSettingsViewModel Remote { get; }

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial SettingsSection Section { get; set; }

    public bool IsDevicesSection => Section == SettingsSection.Devices;

    public bool IsRemoteSection => Section == SettingsSection.Remote;

    partial void OnSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsDevicesSection));
        OnPropertyChanged(nameof(IsRemoteSection));
    }

    [RelayCommand]
    private void Open()
    {
        Devices.Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Show(SettingsSection section) => Section = section;
}

/// <summary>「设备」页里的一行:一台配对过的远程设备。</summary>
public sealed record PairedDeviceItem(string DeviceId, string Name, string Address, DeviceConnection Connection, long PairedAt);

/// <summary>「设备」页:本控制端配对过的设备,和「添加设备」表单(地址 + 配对码)。</summary>
public sealed partial class DevicesSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private readonly DeviceActions _actions;

    public DevicesSettingsViewModel(ClientStores stores, DeviceRegistry devices, DeviceActions actions)
    {
        _stores = stores;
        _devices = devices;
        _actions = actions;
        stores.Paired.Changed += Refresh;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<PairedDeviceItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>宿主支持远程设备(桌面端);模拟数据模式下不显示添加表单。宿主在外壳创建之后才接上,所以每次刷新时重新判断。</summary>
    [ObservableProperty] public partial bool CanAdd { get; private set; }

    [ObservableProperty] public partial string Address { get; set; } = "";

    [ObservableProperty] public partial string Code { get; set; } = "";

    [ObservableProperty] public partial bool IsPairing { get; private set; }

    /// <summary>最近一次添加的结果;成功或还没试过为 null。</summary>
    [ObservableProperty] public partial PairOutcome? LastError { get; private set; }

    /// <summary>刚添加成功(显示一句确认,改输入后消失)。</summary>
    [ObservableProperty] public partial bool JustPaired { get; private set; }

    partial void OnAddressChanged(string value) => ClearResult();

    partial void OnCodeChanged(string value) => ClearResult();

    private void ClearResult()
    {
        LastError = null;
        JustPaired = false;
    }

    [RelayCommand]
    private async Task PairAsync()
    {
        IsPairing = true;
        try
        {
            var outcome = await _actions.PairAsync(Address, Code);
            if (outcome == PairOutcome.Paired)
            {
                Address = "";
                Code = "";
                LastError = null;
                JustPaired = true;
            }
            else
            {
                LastError = outcome;
            }
        }
        finally
        {
            IsPairing = false;
        }
    }

    [RelayCommand]
    private Task RemoveAsync(string deviceId) => _actions.RemoveAsync(deviceId);

    public void Refresh()
    {
        CanAdd = _devices.Remote is not null;
        var workspace = _stores.Workspace.State;
        var items = _stores.Paired.State.Select(d =>
        {
            var live = workspace.Devices.GetValueOrDefault(d.DeviceId);
            return new PairedDeviceItem(d.DeviceId, live?.Name ?? d.Name, $"{d.Host}:{d.Port}",
                live?.Connection ?? DeviceConnection.Connecting, d.PairedAt);
        }).ToList();

        if (items.SequenceEqual(Items))
        {
            return;
        }

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
    }
}

/// <summary>「连接与远程」页里的一行:一个配对过本机的控制端。</summary>
public sealed record ControllerItem(string Fingerprint, string Name, string ShortFingerprint, bool Connected, long PairedAt, long? LastSeenAt);

/// <summary>
/// 「连接与远程」页(本机设备):远程开关、地址、配对码、已配对的控制端。数据只来自 Store 里本机设备的 <see cref="DeviceInfo.Remote"/>
/// (设备是真源);改动经 <see cref="DeviceActions"/> 发给设备,结果再经 remote 主题回来。
/// </summary>
public sealed partial class RemoteSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private readonly DeviceActions _actions;

    public RemoteSettingsViewModel(ClientStores stores, DeviceRegistry devices, DeviceActions actions)
    {
        _stores = stores;
        _devices = devices;
        _actions = actions;
        stores.Workspace.Changed += Refresh;
        Refresh();
    }

    /// <summary>本机设备连着、数据就绪,能读到远程状态。</summary>
    [ObservableProperty] public partial bool IsAvailable { get; private set; }

    [ObservableProperty] public partial bool IsEnabled { get; private set; }

    [ObservableProperty] public partial string? PairingCode { get; private set; }

    /// <summary>打开失败的原因代码(例如 port-unavailable)。</summary>
    [ObservableProperty] public partial string? Error { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    public ObservableCollection<string> Addresses { get; } = [];

    public ObservableCollection<ControllerItem> Controllers { get; } = [];

    public bool HasControllers => Controllers.Count > 0;

    public bool HasAddresses => Addresses.Count > 0;

    /// <summary>开关(双向绑定):写入时把意图发给设备,显示的值仍以设备推回来的为准。</summary>
    public bool Enabled
    {
        get => IsEnabled;
        set
        {
            if (value != IsEnabled)
            {
                _ = SetEnabledAsync(value);
            }
        }
    }

    private async Task SetEnabledAsync(bool value)
    {
        IsBusy = true;
        try
        {
            await _actions.SetRemoteEnabledAsync(value);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    [RelayCommand]
    private Task RotateCodeAsync() => _actions.RotatePairingCodeAsync();

    [RelayCommand]
    private Task RevokeAsync(string fingerprint) => _actions.RevokeControllerAsync(fingerprint);

    private void Refresh()
    {
        var remote = _devices.Local is { } link && _stores.Workspace.State.Devices.TryGetValue(link.DeviceId, out var device) ? device.Remote : null;
        IsAvailable = remote is not null;
        IsEnabled = remote?.Status.Enabled ?? false;
        PairingCode = remote?.Status.PairingCode is { Length: 6 } code ? $"{code[..3]} {code[3..]}" : null;
        Error = remote?.Status.Error;
        OnPropertyChanged(nameof(Enabled));

        var port = remote?.Status.Port;
        var addresses = remote is null || port is null
            ? []
            : remote.Status.Addresses.Select(a => a.Contains(':', StringComparison.Ordinal) ? $"[{a}]:{port}" : $"{a}:{port}").ToList();
        Sync(Addresses, addresses);
        OnPropertyChanged(nameof(HasAddresses));

        var controllers = remote?.Controllers
            .Select(c => new ControllerItem(c.Fingerprint, c.Name, ShortFingerprint(c.Fingerprint), c.Connected, c.PairedAt, c.LastSeenAt))
            .ToList() ?? [];
        Sync(Controllers, controllers);
        OnPropertyChanged(nameof(HasControllers));
    }

    /// <summary>指纹的前 8 位,四位一组(界面上帮用户辨认是哪台设备)。</summary>
    private static string ShortFingerprint(string hex) => hex.Length >= 8 ? $"{hex[..4]} {hex[4..8]}".ToUpperInvariant() : hex;

    private static void Sync<T>(ObservableCollection<T> target, List<T> items)
    {
        if (items.SequenceEqual(target))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
