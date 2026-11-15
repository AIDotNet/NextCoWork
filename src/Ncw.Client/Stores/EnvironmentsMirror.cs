using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上全部执行环境的镜像(<c>environments</c> 主题的全量状态)。真源在设备的 <c>EnvironmentRegistry</c> 和各环境实例(AGENTS.md §3 规则 1,key 带 deviceId):
/// 配置、连接状态(idle / connecting / ready / reconnecting / failed)、最近的错误 key(指纹不一致时带详情)、延迟、是否默认,都在这一份里,不另存。
/// 凭据永远不在这里:只有「有没有」(<c>HasSavedPassword</c> / <c>HasIdentityFile</c>)。
/// </summary>
/// <param name="Known">false = 设备断开或还没拿到快照:状态是<b>未知</b>(规则 7)——界面不把旧的「已连接」当现状,也不判成「已断开」或「失败」。</param>
/// <param name="Rev">设备上快照的单调版本号;只收不更旧的(规则 6)。</param>
public sealed record DeviceEnvironments(bool Known, long Rev, string DefaultId, ImmutableArray<EnvironmentDto> Environments)
{
    public static readonly DeviceEnvironments Unknown = new(false, 0, "local", []);

    public EnvironmentDto? Find(string environmentId) => Environments.FirstOrDefault(e => e.Id == environmentId);
}

public sealed record EnvironmentsMirror(ImmutableDictionary<string, DeviceEnvironments> Devices)
{
    public static readonly EnvironmentsMirror Empty = new(ImmutableDictionary<string, DeviceEnvironments>.Empty);

    public DeviceEnvironments Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceEnvironments.Unknown;

    /// <summary>
    /// 按执行环境标识(设备 + 环境 id)找环境:工具卡片的 <c>tool_start</c> 只带这两个 id,显示名和种类从这里取(一个事实只有一个真源)。
    /// 设备状态未知或环境不在清单里返回 null。
    /// </summary>
    public EnvironmentDto? Find(string deviceId, string environmentId) =>
        Devices.TryGetValue(deviceId, out var device) && device.Known ? device.Find(environmentId) : null;
}

/// <summary>
/// 环境镜像的折叠规则(纯函数)。全量状态,收到不更旧的就整体替换;重放的旧事件不能把状态改回旧值(规则 6);设备断开标为未知(规则 7)——
/// 未知之后设备重启(版本号从头来过)的第一份快照照收。
/// </summary>
public static class EnvironmentsReducer
{
    public static EnvironmentsMirror ApplyTopic(EnvironmentsMirror mirror, string deviceId, EnvironmentsTopicState state)
    {
        var current = mirror.Of(deviceId);
        // 同一个版本号 = 同一份内容(设备上每次发布 Rev 加一):重复收到不改 Store(界面不为同一份状态重绘)
        if (current.Known && state.Rev <= current.Rev)
        {
            return mirror;
        }

        return mirror with { Devices = mirror.Devices.SetItem(deviceId, new DeviceEnvironments(true, state.Rev, state.DefaultId, [.. state.Environments])) };
    }

    public static EnvironmentsMirror MarkUnknown(EnvironmentsMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;
}
