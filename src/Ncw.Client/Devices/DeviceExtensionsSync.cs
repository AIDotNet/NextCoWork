using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

/// <summary>
/// 一条连接上的「扩展域」镜像同步(<see cref="DeviceLink"/> 每次连上后调 <see cref="AttachAsync"/>,断开时调 <see cref="MarkUnknown"/>):
/// <c>extensions</c> 主题(Skills / 命令 / Hooks 的修订号,viewer 起)→ <see cref="ClientStores.ExtensionRevisions"/>;<c>mcp</c> 主题(服务器连接状态,全量 + 单调 Rev,member 起)→ <see cref="ClientStores.Mcp"/>。
/// 清单本身(<c>skills.list</c> 等按项目取)由 <c>ExtensionActions</c> 的刷新器在修订号前进时重拉。设备不支持某个能力(老设备)或角色不够就跳过,不报错。
/// 设备的事实只经这里和那些 Actions 写入这几个 Store(规则 1,key 带 deviceId)。
/// </summary>
internal sealed class DeviceExtensionsSync(ClientStores stores, IUiDispatcher ui, Func<string> deviceId)
{
    public async Task AttachAsync(DeviceClient client, CancellationToken ct)
    {
        var features = client.Welcome.Features;
        if (features.Contains(Capabilities.Skills) || features.Contains(Capabilities.Commands) || features.Contains(Capabilities.Hooks))
        {
            await client.SubscribeAsync(ExtensionsMethods.Topic,
                (state, _) => ui.Post(() => stores.ExtensionRevisions.Update(m => ExtensionRevisionsReducer.Apply(m, deviceId(), state))),
                evt =>
                {
                    if (evt.Type == ExtensionsEvents.Changed)
                    {
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<ExtensionsTopicState>());
                        ui.Post(() => stores.ExtensionRevisions.Update(m => ExtensionRevisionsReducer.Apply(m, deviceId(), state)));
                    }
                }, ct).ConfigureAwait(false);
        }

        if (features.Contains(Capabilities.Mcp) && client.Welcome.Role is "member" or "owner")
        {
            await client.SubscribeAsync(McpMethods.Topic,
                (state, _) => ui.Post(() => stores.Mcp.Update(m => McpReducer.ApplyTopic(m, deviceId(), state))),
                evt =>
                {
                    if (evt.Type == McpEvents.Changed)
                    {
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<McpTopicState>());
                        ui.Post(() => stores.Mcp.Update(m => McpReducer.ApplyTopic(m, deviceId(), state)));
                    }
                }, ct).ConfigureAwait(false);
        }

        // 插件:全量状态 + 挂起的提问(涉及执行的审批带设备 + 环境),member 起;老设备没有这个能力就跳过
        if (features.Contains(Capabilities.Plugins) && client.Welcome.Role is "member" or "owner")
        {
            await client.SubscribeAsync(PluginsMethods.Topic,
                (state, _) => ui.Post(() => stores.Plugins.Update(m => PluginsReducer.ApplyTopic(m, deviceId(), state))),
                evt =>
                {
                    if (evt.Type == PluginsEvents.Changed)
                    {
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<PluginsTopicState>());
                        ui.Post(() => stores.Plugins.Update(m => PluginsReducer.ApplyTopic(m, deviceId(), state)));
                    }
                }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>设备断开:这台设备上的修订号、清单、MCP / 插件状态都标为未知(规则 7),不把旧值当现状。</summary>
    public void MarkUnknown()
    {
        var id = deviceId();
        ui.Post(() =>
        {
            stores.ExtensionRevisions.Update(m => ExtensionRevisionsReducer.MarkUnknown(m, id));
            stores.Skills.Update(m => SkillsReducer.MarkDeviceUnknown(m, id));
            stores.Commands.Update(m => CommandsReducer.MarkDeviceUnknown(m, id));
            stores.Hooks.Update(m => HooksReducer.MarkDeviceUnknown(m, id));
            stores.Mcp.Update(m => McpReducer.MarkUnknown(m, id));
            stores.Plugins.Update(m => PluginsReducer.MarkUnknown(m, id));
        });
    }
}
