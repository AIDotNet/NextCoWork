using System.Collections.Immutable;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Session;

namespace Ncw.Client.Devices;

/// <summary>
/// 本控制端配对过的远程设备(docs/design/09 §3):配对、按钉扎的公钥连接、移除。
/// 已配对列表的真源是 <see cref="ClientStores.Paired"/>,每次变化立刻写回 <see cref="IDeviceBook"/>;
/// 每台设备一个 <see cref="DeviceLink"/>,设备的事实照常写进 Workspace Store(按 deviceId 区分)。
/// </summary>
public sealed class RemoteDeviceManager(
    ClientStores stores,
    IUiDispatcher ui,
    IControllerIdentityStore identities,
    IDeviceBook book,
    string controllerName,
    TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly Dictionary<string, DeviceLink> _links = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>读出保存的设备并开始连接。</summary>
    public void Start()
    {
        var saved = book.Load();
        stores.Paired.Update(_ => saved);
        foreach (var device in saved)
        {
            Connect(device);
        }
    }

    public DeviceLink? Find(string deviceId)
    {
        lock (_gate)
        {
            return _links.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>
    /// 用 <c>IP:端口</c> 和配对码配对一台设备,成功后保存并连接。已经配对过的同一台设备会被替换(例如被吊销后重新配对)。
    /// 失败抛 <see cref="PairingException"/>(码不对、设备没验证通过、远程关了)、<see cref="Protocol.Security.SecureChannelException"/>
    /// 或网络异常;身份无法安全保存时抛 <see cref="PlatformNotSupportedException"/>。
    /// </summary>
    public async Task<SavedDevice> PairAsync(string host, int port, string code, CancellationToken ct = default)
    {
        var identity = identities.LoadOrCreate();
        var paired = await RemoteDevices.PairAsync(host, port, identity, code, controllerName, ct).ConfigureAwait(false);
        var saved = new SavedDevice(paired.DeviceId, $"{host}:{port}", Convert.ToHexStringLower(paired.DeviceFingerprint),
            host, port, paired.Role, paired.RouteToken, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());

        await DisconnectAsync(paired.DeviceId).ConfigureAwait(false);
        ui.Post(() =>
        {
            stores.Paired.Update(list => [.. list.Where(d => d.DeviceId != saved.DeviceId), saved]);
            book.Save(stores.Paired.State);
        });
        Connect(saved);
        return saved;
    }

    /// <summary>忘掉一台设备:断开、从列表和 Store 里去掉它和它的项目。不通知设备(设备上的信任记录由设备主人吊销)。</summary>
    public async Task RemoveAsync(string deviceId)
    {
        await DisconnectAsync(deviceId).ConfigureAwait(false);
        ui.Post(() =>
        {
            stores.Paired.Update(list => [.. list.Where(d => d.DeviceId != deviceId)]);
            book.Save(stores.Paired.State);
            stores.Workspace.Update(s => s with
            {
                Devices = s.Devices.Remove(deviceId),
                Projects = s.Projects.RemoveRange(s.Projects.Keys.Where(p => p.DeviceId == deviceId)),
            });
        });
    }

    private void Connect(SavedDevice device)
    {
        var identity = identities.LoadOrCreate();
        var fingerprint = Convert.FromHexString(device.Fingerprint);
        var link = new DeviceLink(stores, ui, device.DeviceId, DeviceKind.Remote, displayName: null,
            ct => RemoteDevices.ConnectAsync(device.Host, device.Port, identity, fingerprint, ct),
            new ClientHello(controllerName, typeof(RemoteDeviceManager).Assembly.GetName().Version?.ToString() ?? "3.0.0"),
            initialName: device.Name);
        lock (_gate)
        {
            _links[device.DeviceId] = link;
        }

        link.Start();
    }

    private async Task DisconnectAsync(string deviceId)
    {
        DeviceLink? link;
        lock (_gate)
        {
            _links.Remove(deviceId, out link);
        }

        if (link is not null)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<DeviceLink> links;
        lock (_gate)
        {
            links = [.. _links.Values];
            _links.Clear();
        }

        foreach (var link in links)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }
}
