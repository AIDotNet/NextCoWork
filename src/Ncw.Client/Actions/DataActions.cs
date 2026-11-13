using Ncw.Client.Devices;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>设备数据相关的用户意图(接管页上的「重试」)。</summary>
public sealed class DataActions(DeviceRegistry devices)
{
    /// <summary>重试本机设备的数据准备。结果经 data 主题进入 Store;连接断了就等重连后的快照。</summary>
    public async Task RetryLocalAsync()
    {
        if (devices.Local is not { IsConnected: true } link)
        {
            return;
        }

        try
        {
            await link.RetryDataAsync();
        }
        catch (ProtocolException)
        {
            // 失败的原因同样在 data 主题里,界面照 Store 显示。
        }
    }
}
