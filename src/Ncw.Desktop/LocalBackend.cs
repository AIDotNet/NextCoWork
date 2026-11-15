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
    private readonly LocalAgentSidecar _sidecar;
    private DeviceLink? _link;
    private RemoteDeviceManager? _remote;

    public LocalBackend()
        : this(new LocalAgentSidecar(LocalAgentSidecar.DefaultDataRoot))
    {
    }

    /// <summary>用指定的守护(测试和开发用的冒烟程序:假版本号、指定的数据根等)。</summary>
    public LocalBackend(LocalAgentSidecar sidecar) => _sidecar = sidecar;

    /// <summary>本机 Agent 的守护(状态、重试)。设置页和诊断页接入时读它。</summary>
    public LocalAgentSidecar Sidecar => _sidecar;

    private IStartupHost? _startupHost;

    /// <summary>启动加载界面的宿主能力:守护的重试和诊断(docs/design/03 §10)。</summary>
    public IStartupHost StartupHost => _startupHost ??= new SidecarStartupHost(_sidecar);

    public void Start(ClientStores stores, DeviceRegistry devices, IUiDispatcher ui, IClientText text)
    {
        var endpoint = LocalTransport.DefaultEndpoint(_sidecar.DataRoot);
        // 状态先落到跟踪日志:界面文案(版本不匹配、反复崩溃、数据目录被占用)由 UI 批次按 SidecarIssue 接入。
        _sidecar.StatusChanged += status =>
            Trace.WriteLine($"[sidecar] {status.Phase} issue={status.Issue} agent={status.AgentVersion} restarts={status.RestartCount} {status.Detail}");
        // 守护的真实信号(进程拉起、握手成功、各种失败)写进启动事实,启动加载界面据此显示阶段与失败(必须在 Start 之前接上)
        StartupBridge.Attach(_sidecar, new StartupSignals(stores, ui));
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
