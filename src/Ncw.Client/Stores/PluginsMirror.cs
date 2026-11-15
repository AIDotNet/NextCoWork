using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上插件的镜像(<c>plugins</c> 主题的全量状态)。真源在设备的 <c>PluginManager</c>(AGENTS.md §3 规则 1,key 带 deviceId)。
/// 这里<b>没有任何凭据</b>:清单、授权、诊断、挂起的提问都是结构化数据;活动日志(只有摘要)不进 Store,按需用 <c>PluginsActions.ActivityAsync</c> 取。
/// </summary>
/// <param name="Known">false = 设备断开或还没拿到快照,状态是未知(规则 7):界面不把旧的「运行中」当现状,也不继续显示它的命令 / 工具。</param>
/// <param name="Rev">设备上快照的单调版本号;只收不更旧的(规则 6)。</param>
/// <param name="Interactions">插件正在等用户回答的提问(确认 / 输入 / 选择 / 权限申请 / 执行命令的审批,涉及执行的带设备 + 环境),权威状态也在设备上,随快照一起到。</param>
public sealed record DevicePlugins(bool Known, long Rev, string ApiVersion, ImmutableArray<PluginInfoDto> Plugins, ImmutableArray<PluginInteractionDto> Interactions)
{
    public static readonly DevicePlugins Unknown = new(false, 0, "", [], []);

    public PluginInfoDto? Find(string pluginId) => Plugins.FirstOrDefault(p => p.Id == pluginId);

    /// <summary>某个插件挂起的提问(界面据此在插件详情里弹出)。</summary>
    public IEnumerable<PluginInteractionDto> InteractionsOf(string pluginId) => Interactions.Where(i => i.PluginId == pluginId);
}

public sealed record PluginsMirror(ImmutableDictionary<string, DevicePlugins> Devices)
{
    public static readonly PluginsMirror Empty = new(ImmutableDictionary<string, DevicePlugins>.Empty);

    public DevicePlugins Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DevicePlugins.Unknown;
}

/// <summary>
/// 插件镜像的折叠规则(纯函数)。全量状态,收到不更旧的就整体替换;重放的旧事件不能把状态改回旧值(规则 6);设备断开标为未知(规则 7)——
/// 未知之后设备重启(版本号从头来过)的第一份快照照收。<c>topic</c> 的事件和每个变更方法的返回值是同一个类型,走同一个函数,所以先到后到都不会写出旧状态。
/// </summary>
public static class PluginsReducer
{
    public static PluginsMirror ApplyTopic(PluginsMirror mirror, string deviceId, PluginsTopicState state)
    {
        var current = mirror.Of(deviceId);
        if (current.Known && state.Rev <= current.Rev)
        {
            return mirror; // 不更新的(含同一个版本号 = 同一份内容):不改 Store,界面不为同一份状态重绘
        }

        return mirror with
        {
            Devices = mirror.Devices.SetItem(deviceId, new DevicePlugins(
                true, state.Rev, state.ApiVersion, [.. state.Plugins.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal)], [.. state.Interactions])),
        };
    }

    public static PluginsMirror MarkUnknown(PluginsMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;
}
