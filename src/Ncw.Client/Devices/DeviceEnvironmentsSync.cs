using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

/// <summary>
/// 一条连接上的「执行环境」镜像同步(<see cref="DeviceLink"/> 每次连上后调 <see cref="AttachAsync"/>,断开时调 <see cref="MarkUnknown"/>):
/// <c>environments</c> 主题(全量 + 单调 Rev,member 起)→ <see cref="ClientStores.Environments"/>。设备不支持这个能力(老设备)或角色不够(viewer)就跳过,不报错。
/// 设备的事实只经这里写进这个 Store(规则 1,key 带 deviceId);用户的意图只经 <c>EnvironmentActions</c>(规则 4)。
/// </summary>
internal sealed class DeviceEnvironmentsSync(ClientStores stores, IUiDispatcher ui, Func<string> deviceId)
{
    public async Task AttachAsync(DeviceClient client, CancellationToken ct)
    {
        if (!client.Welcome.Features.Contains(Capabilities.Environments) || client.Welcome.Role is not ("member" or "owner"))
        {
            return;
        }

        await client.SubscribeAsync(EnvironmentsMethods.Topic,
            (state, _) => ui.Post(() => stores.Environments.Update(m => EnvironmentsReducer.ApplyTopic(m, deviceId(), state))),
            evt =>
            {
                if (evt.Type == EnvironmentsEvents.Changed)
                {
                    var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<EnvironmentsTopicState>());
                    ui.Post(() => stores.Environments.Update(m => EnvironmentsReducer.ApplyTopic(m, deviceId(), state)));
                }
            }, ct).ConfigureAwait(false);
    }

    /// <summary>设备断开:这台设备上环境的状态标为未知(规则 7),不把旧值当现状。</summary>
    public void MarkUnknown()
    {
        var id = deviceId();
        ui.Post(() => stores.Environments.Update(m => EnvironmentsReducer.MarkUnknown(m, id)));
    }
}
