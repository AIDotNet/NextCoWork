using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.UI;

namespace Ncw.Desktop;

/// <summary>桌面端的后端:拉起本机 Agent,并用 DeviceLink 把它同步进 Store。</summary>
public sealed class LocalBackend : INcwBackend
{
    private readonly LocalAgentSidecar _sidecar = new(LocalAgentSidecar.DefaultDataRoot);
    private DeviceLink? _link;

    public void Start(ClientStores stores, DeviceRegistry devices, IUiDispatcher ui, IClientText text)
    {
        var endpoint = LocalTransport.DefaultEndpoint(_sidecar.DataRoot);
        _sidecar.Start();
        _link = new DeviceLink(stores, ui, "local", DeviceKind.Local, text.LocalDeviceName,
            async ct =>
            {
                await _sidecar.WaitReadyAsync(ct).ConfigureAwait(false);
                return await LocalTransport.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            },
            new ClientHello($"desktop-{Environment.MachineName}", typeof(LocalBackend).Assembly.GetName().Version?.ToString() ?? "3.0.0",
                LocalToken: _sidecar.Token));
        devices.Local = _link;
        _link.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_link is not null)
        {
            await _link.DisposeAsync().ConfigureAwait(false);
        }

        await _sidecar.DisposeAsync().ConfigureAwait(false);
    }
}
