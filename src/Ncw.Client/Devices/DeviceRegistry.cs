namespace Ncw.Client.Devices;

/// <summary>控制端当前连着的设备。3.0 先只有本机;云端和局域网设备以后加进来。</summary>
public sealed class DeviceRegistry
{
    /// <summary>桌面内置的本机 Agent。模拟数据模式下为 null。</summary>
    public DeviceLink? Local { get; set; }

    /// <summary>配对过的远程设备。宿主不支持远程设备(或模拟数据模式)时为 null。</summary>
    public RemoteDeviceManager? Remote { get; set; }

    /// <summary>按 deviceId 找到对应的连接(本机或远程)。</summary>
    public DeviceLink? Find(string deviceId) =>
        Local is { } local && local.DeviceId == deviceId ? local : Remote?.Find(deviceId);
}
