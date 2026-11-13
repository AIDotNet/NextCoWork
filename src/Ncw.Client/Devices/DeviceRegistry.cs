namespace Ncw.Client.Devices;

/// <summary>控制端当前连着的设备。3.0 先只有本机;云端和局域网设备以后加进来。</summary>
public sealed class DeviceRegistry
{
    /// <summary>桌面内置的本机 Agent。模拟数据模式下为 null。</summary>
    public DeviceLink? Local { get; set; }
}
