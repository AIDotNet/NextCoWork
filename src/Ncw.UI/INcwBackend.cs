using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;

namespace Ncw.UI;

/// <summary>
/// 应用连接设备的方式,由宿主提供:桌面端拉起本机 Agent(sidecar)并连接;以后的手机端只连远程设备。
/// </summary>
public interface INcwBackend : IAsyncDisposable
{
    void Start(ClientStores stores, DeviceRegistry devices, IUiDispatcher ui, IClientText text);
}
