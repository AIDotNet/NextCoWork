using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>推送在设置里的状态(界面按它写文字)。</summary>
public enum PushStatus
{
    /// <summary>这个平台没有推送(桌面 / 模拟器 / 宿主没接)。</summary>
    Unsupported,

    /// <summary>还没问过通知权限。</summary>
    NotDetermined,

    /// <summary>用户拒绝了通知权限(引导去系统设置)。</summary>
    Denied,

    /// <summary>有权限但平台暂时没给令牌。</summary>
    NoToken,

    /// <summary>令牌已拿到,正在登记到设备。</summary>
    Registering,

    /// <summary>令牌已登记到至少一台设备。</summary>
    Registered,

    /// <summary>登记失败(设备不支持 / 角色不够 / 设备拒绝);设备重连后会再试。</summary>
    Failed,
}

/// <summary>
/// 推送登记的协调者(docs/design/02 A4:用户不在 App 里时的审批 / 完成通知)。拿到令牌后经 <c>push.register</c> 登记到每台<b>已连接且声明了推送能力</b>的设备;
/// 设备断开又连上(或令牌被平台刷新)后重新登记——设备上的登记表是真源,这里只记「这个令牌已经登记到哪些设备」避免重复发请求。
/// 只读 Store(设备连接状态),不订阅协议事件。
/// </summary>
public sealed partial class PushViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly IPushCommands _actions;
    private readonly Func<IPushRegistration?> _push;
    private readonly string? _appVersion;
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private string? _token;
    private SynchronizationContext? _context;

    public PushViewModel(ClientStores stores, IPushCommands actions, Func<IPushRegistration?> push, string? appVersion = null)
    {
        _stores = stores;
        _actions = actions;
        _push = push;
        _appVersion = appVersion;
        stores.Workspace.Changed += OnWorkspaceChanged;
        Status = Compute(null);
    }

    [ObservableProperty] public partial PushStatus Status { get; private set; }

    /// <summary>已登记到的设备数。</summary>
    [ObservableProperty] public partial int RegisteredDevices { get; private set; }

    public bool CanEnable => Status is PushStatus.NotDetermined or PushStatus.NoToken or PushStatus.Failed;

    public bool IsSupported => _push() is { IsSupported: true };

    /// <summary>设置页打开时:读权限 / 令牌,拿得到就登记。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_push() is not { IsSupported: true } push)
        {
            Status = PushStatus.Unsupported;
            return;
        }

        _context = SynchronizationContext.Current; // 令牌刷新事件可能在别的线程:Store 只能在 UI 线程读,回到这里再处理
        push.TokenRefreshed -= OnTokenRefreshed;
        push.TokenRefreshed += OnTokenRefreshed;
        var permission = await push.GetPermissionAsync(ct);
        if (permission == PushPermission.Granted)
        {
            _token = await push.GetTokenAsync(ct);
        }

        await SyncAsync(permission);
    }

    /// <summary>「开启通知」:请求权限(首次会弹系统对话框),成功后取令牌并登记。</summary>
    [RelayCommand]
    private async Task EnableAsync()
    {
        if (_push() is not { IsSupported: true } push)
        {
            return;
        }

        var permission = await push.RequestPermissionAsync();
        _token = permission == PushPermission.Granted ? await push.GetTokenAsync() : null;
        await SyncAsync(permission);
    }

    private void OnTokenRefreshed(string token)
    {
        // 平台换了令牌:旧登记作废,重新登记
        void Apply()
        {
            _token = token;
            _registered.Clear();
            _ = SyncAsync(PushPermission.Granted);
        }

        if (_context is { } context)
        {
            context.Post(_ => Apply(), null);
        }
        else
        {
            Apply();
        }
    }

    private void OnWorkspaceChanged()
    {
        // 设备断开 → 忘掉它的登记标记,重连后再登记一次
        var connected = _stores.Workspace.State.Devices.Where(d => d.Value.Connection == DeviceConnection.Connected).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        _registered.RemoveWhere(id => !connected.Contains(id));
        if (_token is not null)
        {
            _ = SyncAsync(PushPermission.Granted);
        }
    }

    private async Task SyncAsync(PushPermission permission)
    {
        var push = _push();
        if (push is not { IsSupported: true })
        {
            Status = PushStatus.Unsupported;
            return;
        }

        if (permission != PushPermission.Granted || _token is null)
        {
            Status = Compute(permission);
            return;
        }

        var failed = false;
        foreach (var (deviceId, device) in _stores.Workspace.State.Devices)
        {
            if (device.Connection != DeviceConnection.Connected || _registered.Contains(deviceId) || !_inFlight.Add(deviceId) || !_actions.Supports(deviceId))
            {
                continue;
            }

            Status = PushStatus.Registering;
            try
            {
                var result = await _actions.RegisterAsync(deviceId, push.Platform, _token, _appVersion);
                if (result.IsOk)
                {
                    _registered.Add(deviceId);
                }
                else
                {
                    failed = true;
                }
            }
            finally
            {
                _inFlight.Remove(deviceId);
            }
        }

        RegisteredDevices = _registered.Count;
        Status = _registered.Count > 0 ? PushStatus.Registered : failed ? PushStatus.Failed : PushStatus.NoToken;
        OnPropertyChanged(nameof(CanEnable));
    }

    private PushStatus Compute(PushPermission? permission) => _push() is not { IsSupported: true }
        ? PushStatus.Unsupported
        : permission switch
        {
            PushPermission.Denied => PushStatus.Denied,
            PushPermission.NotDetermined => PushStatus.NotDetermined,
            _ => PushStatus.NoToken,
        };

    partial void OnStatusChanged(PushStatus value) => OnPropertyChanged(nameof(CanEnable));
}
