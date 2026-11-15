using System.Diagnostics;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Desktop.Sidecar;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.UI;

namespace Ncw.Desktop;

/// <summary>桌面端的后端:拉起本机 Agent,并用 DeviceLink 把它同步进 Store。</summary>
public sealed class LocalBackend : INcwBackend
{
    private readonly LocalAgentSidecar _sidecar = new(LocalAgentSidecar.DefaultDataRoot);
    private DeviceLink? _link;
    private RemoteDeviceManager? _remote;

    /// <summary>本机 Agent 的守护(状态、重试)。设置页和诊断页接入时读它。</summary>
    public LocalAgentSidecar Sidecar => _sidecar;

    public void Start(ClientStores stores, DeviceRegistry devices, IUiDispatcher ui, IClientText text)
    {
        var endpoint = LocalTransport.DefaultEndpoint(_sidecar.DataRoot);
        // 状态先落到跟踪日志:界面文案(版本不匹配、反复崩溃、数据目录被占用)由 UI 批次按 SidecarIssue 接入。
        _sidecar.StatusChanged += status =>
            Trace.WriteLine($"[sidecar] {status.Phase} issue={status.Issue} agent={status.AgentVersion} restarts={status.RestartCount} {status.Detail}");
        _sidecar.Start();
        _link = new DeviceLink(stores, ui, "local", DeviceKind.Local, text.LocalDeviceName,
            async ct =>
            {
                await _sidecar.WaitReadyAsync(ct).ConfigureAwait(false);
                return await LocalTransport.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            },
            new ClientHello($"desktop-{Environment.MachineName}", SidecarVersionPolicy.OfAssembly(typeof(LocalBackend).Assembly),
                LocalToken: _sidecar.Token));
        devices.Local = _link;
        _link.Start();

        // 配对过的远程设备:控制端身份放系统钥匙串,设备列表放客户端目录(docs/design/09 §3、§4)。
        _remote = new RemoteDeviceManager(stores, ui, new ControllerKeyStore(NcwApp.ClientDirectory),
            new JsonFileDeviceBook(Path.Combine(NcwApp.ClientDirectory, "devices.json")), Environment.MachineName);
        devices.Remote = _remote;
        _remote.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_remote is not null)
        {
            await _remote.DisposeAsync().ConfigureAwait(false);
        }

        if (_link is not null)
        {
            await _link.DisposeAsync().ConfigureAwait(false);
        }

        await _sidecar.DisposeAsync().ConfigureAwait(false);
    }
}
