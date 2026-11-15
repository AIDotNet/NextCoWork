using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上 MCP 服务器的镜像(<c>mcp</c> 主题的全量状态)。真源在设备的 <c>McpService</c>(AGENTS.md §3 规则 1,key 带 deviceId)。
/// 这里<b>没有任何密钥值</b>:配置只有环境变量 / 请求头的键名,状态只有稳定的错误 key;「哪些键已设置」只经 <c>McpActions.GetSecretsInfoAsync</c> 单次取回,不进 Store。
/// </summary>
/// <param name="Known">false = 设备断开或还没拿到快照,状态是未知(规则 7):界面不把旧的「已连接」当现状,也不继续显示「N 个工具」。</param>
/// <param name="Rev">设备上快照的单调版本号;只收不更旧的(规则 6)。</param>
public sealed record DeviceMcp(bool Known, long Rev, ImmutableArray<McpServerStatusDto> Servers)
{
    public static readonly DeviceMcp Unknown = new(false, 0, []);

    public McpServerStatusDto? Find(string id) => Servers.FirstOrDefault(s => s.Config.Id == id);
}

public sealed record McpMirror(ImmutableDictionary<string, DeviceMcp> Devices)
{
    public static readonly McpMirror Empty = new(ImmutableDictionary<string, DeviceMcp>.Empty);

    public DeviceMcp Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceMcp.Unknown;
}

/// <summary>
/// MCP 镜像的折叠规则(纯函数)。全量状态,收到不更旧的就整体替换;重放的旧事件不能把状态改回旧值(规则 6);设备断开标为未知(规则 7)——
/// 未知之后设备重启(版本号从头来过)的第一份快照照收。
/// </summary>
public static class McpReducer
{
    public static McpMirror ApplyTopic(McpMirror mirror, string deviceId, McpTopicState state)
    {
        var current = mirror.Of(deviceId);
        if (current.Known && state.Rev < current.Rev)
        {
            return mirror;
        }

        // 同一个版本号 = 同一份内容(设备上每次发布 Rev 加一):重复收到不改 Store(界面不为同一份状态重绘)
        if (current.Known && state.Rev == current.Rev)
        {
            return mirror;
        }

        return mirror with { Devices = mirror.Devices.SetItem(deviceId, new DeviceMcp(true, state.Rev, [.. state.Servers.OrderBy(s => s.Config.Id, StringComparer.Ordinal)])) };
    }

    public static McpMirror MarkUnknown(McpMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;
}
