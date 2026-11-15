using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

/// <summary>
/// 一条连接上的「设置 / 定时任务」镜像同步(<see cref="DeviceLink"/> 每次连上后调 <see cref="AttachAsync"/>):订阅 <c>settings</c>(member 起)和 <c>schedules</c>,
/// 把全量状态折进 <see cref="ClientStores.Settings"/> / <see cref="ClientStores.Schedules"/>。设备不支持某个能力(老设备)或角色不够就跳过,不报错。
/// 设备的事实只经这里写入这两个 Store(规则 1)。
/// </summary>
internal sealed class DeviceServicesSync(ClientStores stores, IUiDispatcher ui, Func<string> deviceId)
{
    public async Task AttachAsync(DeviceClient client, CancellationToken ct)
    {
        var role = client.Welcome.Role;
        var features = client.Welcome.Features;
        if (features.Contains(Capabilities.Settings) && role is "member" or "owner")
        {
            await client.SubscribeAsync(SettingsMethods.Topic,
                (state, _) => ui.Post(() => stores.Settings.Update(m => ServicesReducer.ApplySettings(m, deviceId(), state))),
                evt =>
                {
                    if (evt.Type == SettingsEvents.Changed)
                    {
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<DeviceSettingsState>());
                        ui.Post(() => stores.Settings.Update(m => ServicesReducer.ApplySettings(m, deviceId(), state)));
                    }
                }, ct).ConfigureAwait(false);
        }

        if (features.Contains(Capabilities.Schedules))
        {
            await client.SubscribeAsync(SchedulesMethods.Topic,
                (state, _) => ui.Post(() => stores.Schedules.Update(m => ServicesReducer.ApplySchedules(m, deviceId(), state))),
                evt =>
                {
                    if (evt.Type == SchedulesEvents.Changed)
                    {
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<SchedulesTopicState>());
                        ui.Post(() => stores.Schedules.Update(m => ServicesReducer.ApplySchedules(m, deviceId(), state)));
                    }
                }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>设备断开:设置和定时任务标为未知(规则 7),不把旧值当现状;「运行中」的定时运行不判成结束,也不继续算运行中。</summary>
    public void MarkUnknown()
    {
        var id = deviceId();
        ui.Post(() =>
        {
            stores.Settings.Update(m => ServicesReducer.MarkSettingsUnknown(m, id));
            stores.Schedules.Update(m => ServicesReducer.MarkSchedulesUnknown(m, id));
        });
    }
}
