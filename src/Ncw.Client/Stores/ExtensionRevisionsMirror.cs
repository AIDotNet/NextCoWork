using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上扩展域(Skills / 命令 / Hooks)修订号的镜像(<c>extensions</c> 主题)。修订号变了,说明别的控制端(或设备自己)改了配置,
/// 持有这些列表的镜像据此重拉。<see cref="Known"/> = false:设备断开 / 还没拿到快照,修订号是未知的(规则 7)——重连后的首帧会把它重新带回来,
/// 而重连之后的修订号只可能不更旧,所以不需要「断开时清零」。
/// </summary>
public sealed record DeviceExtensionRevisions(bool Known, long Skills, long Commands, long Hooks)
{
    public static readonly DeviceExtensionRevisions Unknown = new(false, 0, 0, 0);
}

public sealed record ExtensionRevisionsMirror(ImmutableDictionary<string, DeviceExtensionRevisions> Devices)
{
    public static readonly ExtensionRevisionsMirror Empty = new(ImmutableDictionary<string, DeviceExtensionRevisions>.Empty);

    public DeviceExtensionRevisions Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceExtensionRevisions.Unknown;
}

/// <summary>修订号镜像的折叠规则(纯函数):逐域取最大(只收不更旧的,重放的旧事件不能把修订号改回去,规则 6);断开标为未知(规则 7)。</summary>
public static class ExtensionRevisionsReducer
{
    public static ExtensionRevisionsMirror Apply(ExtensionRevisionsMirror mirror, string deviceId, ExtensionsTopicState state)
    {
        var current = mirror.Of(deviceId);
        var next = new DeviceExtensionRevisions(
            true, Math.Max(current.Skills, state.SkillsRev), Math.Max(current.Commands, state.CommandsRev), Math.Max(current.Hooks, state.HooksRev));
        return next == current ? mirror : mirror with { Devices = mirror.Devices.SetItem(deviceId, next) };
    }

    public static ExtensionRevisionsMirror MarkUnknown(ExtensionRevisionsMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;
}
