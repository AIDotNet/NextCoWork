using System.Collections.Immutable;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

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
    TimeProvider? clock = null,
    HeartbeatOptions? heartbeat = null) : IAsyncDisposable
{
    private readonly Dictionary<string, DeviceLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _relays = new(StringComparer.Ordinal);
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
        return await SaveAsync(paired).ConfigureAwait(false);
    }

    /// <summary>扫二维码配对(内容是 <c>nextcowork://pair?…</c>)。结果和 <see cref="PairAsync"/> 一样。二维码内容不合法抛 <see cref="FormatException"/>。</summary>
    public async Task<SavedDevice> PairWithQrAsync(string qrContent, CancellationToken ct = default)
    {
        if (!Protocol.Security.PairingQr.TryParse(qrContent, out var qr))
        {
            throw new FormatException("Not a NextCoWork pairing QR code");
        }

        var identity = identities.LoadOrCreate();
        var paired = await RemoteDevices.PairWithQrAsync(qr, identity, controllerName, clock, ct).ConfigureAwait(false);
        return await SaveAsync(paired).ConfigureAwait(false);
    }

    private async Task<SavedDevice> SaveAsync(PairedDevice paired)
    {
        var saved = new SavedDevice(paired.DeviceId, $"{paired.Host}:{paired.Port}", Convert.ToHexStringLower(paired.DeviceFingerprint),
            paired.Host, paired.Port, paired.Role, paired.RouteToken, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(),
            RelayAddress.TryNormalize(paired.Relay, out var relay) ? RelayAddress.ToText(relay) : null);

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
            async ct =>
            {
                // 并行尝试局域网和中继,先通的胜出,局域网优先(09 §4)。每次重连都重新选路:直连断了会续连到中继,回到局域网后又走直连。
                // 中继地址取最新记录的(设备后来才配置中继时,下面会把它学进来)。
                var current = CurrentRelay(device);
                var connection = await RemoteDevices.ConnectAsync(device.DeviceId, AddressesOf(device, current), identity, fingerprint, ct).ConfigureAwait(false);
                return connection.Stream;
            },
            new ClientHello(controllerName, typeof(RemoteDeviceManager).Assembly.GetName().Version?.ToString() ?? "3.0.0"),
            initialName: device.Name, heartbeat: heartbeat);
        link.Synced += () => _ = LearnRelayAsync(device.DeviceId, link);
        lock (_gate)
        {
            _links[device.DeviceId] = link;
        }

        link.Start();
    }

    /// <summary>该设备最新记录的中继地址(记录可能在连接过程中被 <see cref="LearnRelayAsync"/> 更新)。</summary>
    private string? CurrentRelay(SavedDevice fallback)
    {
        lock (_gate)
        {
            return _relays.TryGetValue(fallback.DeviceId, out var relay) ? relay : fallback.Relay;
        }
    }

    private static DeviceAddresses AddressesOf(SavedDevice device, string? relay) =>
        new(device.Host.Length > 0 && device.Port > 0 ? [new LanAddress(device.Host, device.Port)] : [],
            RelayAddress.TryNormalize(relay, out var url) && device.RouteToken.Length > 0 ? new RelayRoute(url, device.RouteToken) : null);

    /// <summary>
    /// 连上之后问设备当前的中继地址(<c>device.info</c>):设备后来才配置(或换了)中继时,已配对的控制端据此学到,离开局域网后就能经中继连。
    /// 记录变了就保存。失败不影响连接。
    /// </summary>
    private async Task LearnRelayAsync(string deviceId, DeviceLink link)
    {
        try
        {
            var info = await link.CallAsync(DeviceMethods.Info, Unit.Value).ConfigureAwait(false);
            var learned = RelayAddress.TryNormalize(info.Relay, out var url) ? RelayAddress.ToText(url) : null;
            lock (_gate)
            {
                _relays[deviceId] = learned;
            }

            ui.Post(() =>
            {
                var list = stores.Paired.State;
                var index = -1;
                for (var i = 0; i < list.Length; i++)
                {
                    if (list[i].DeviceId == deviceId)
                    {
                        index = i;
                    }
                }

                if (index < 0 || list[index].Relay == learned)
                {
                    return;
                }

                stores.Paired.Update(l => l.SetItem(index, l[index] with { Relay = learned }));
                book.Save(stores.Paired.State);
            });
        }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            // 连接中途断了:下一次连上再学。
        }
    }

    private async Task DisconnectAsync(string deviceId)
    {
        DeviceLink? link;
        lock (_gate)
        {
            _links.Remove(deviceId, out link);
            _relays.Remove(deviceId);
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

    /// <summary>
    /// 手机进入后台 / 网络消失:断开所有设备,设备标为「状态未知」。已配对列表不动。之后用 <see cref="Resume"/> 重新连接;可以反复调用。
    /// </summary>
    public async Task SuspendAsync()
    {
        List<DeviceLink> links;
        lock (_gate)
        {
            links = [.. _links.Values];
            _links.Clear();
        }

        foreach (var link in links)
        {
            await link.SuspendAsync().ConfigureAwait(false);
        }
    }

    /// <summary>从 <see cref="SuspendAsync"/> 恢复:重新读出已配对设备并连接(同 <see cref="Start"/>)。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_links.Count > 0)
            {
                return; // 已经在连(重复调用不重复建连接)
            }
        }

        Start();
    }

    /// <summary>丢弃现有连接并立刻重连(网络切换、长时间后台之后连接可能已经失效)。</summary>
    public async Task ReconnectAsync()
    {
        await SuspendAsync().ConfigureAwait(false);
        Resume();
    }
}
