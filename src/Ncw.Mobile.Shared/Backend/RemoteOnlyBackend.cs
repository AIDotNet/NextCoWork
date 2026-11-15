using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.UI;

namespace Ncw.Mobile;

/// <summary>
/// 手机的后端(docs/design/11 §1):没有本机 Agent(02 A4),只创建 <see cref="RemoteDeviceManager"/> —— <c>Start</c> 里读出已配对设备并连接。
/// 控制端身份放系统密钥库(<see cref="IControllerIdentityStore"/>),已配对设备列表放应用私有目录。
/// 同时是 <see cref="IConnectionController"/>:后台 / 网络切换时由 <see cref="ConnectionSupervisor"/> 暂停和恢复所有连接。
/// </summary>
public sealed class RemoteOnlyBackend(IControllerIdentityStore identities, string devicesFilePath, string controllerName) : INcwBackend, IConnectionController
{
    private RemoteDeviceManager? _remote;

    public void Start(ClientStores stores, DeviceRegistry devices, IUiDispatcher ui, IClientText text)
    {
        _remote = new RemoteDeviceManager(stores, ui, identities, new JsonFileDeviceBook(devicesFilePath), controllerName);
        devices.Remote = _remote;
        _remote.Start();
    }

    public Task SuspendAsync() => _remote?.SuspendAsync() ?? Task.CompletedTask;

    public void Resume() => _remote?.Resume();

    public Task ReconnectAsync() => _remote?.ReconnectAsync() ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _remote?.DisposeAsync() ?? ValueTask.CompletedTask;
}
