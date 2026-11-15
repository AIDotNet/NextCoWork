using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上设备设置的镜像(<c>settings</c> 主题)。真源在设备的 <c>SettingsService</c>(AGENTS.md §3 规则 1,key 带 deviceId);「本应用」作用域的设置(外观、快捷键、通知)不在这里——它们是 <c>UiPrefs</c>,不进协议。
/// </summary>
/// <param name="Known">false = 设备断开或还没拿到快照,状态是未知(规则 7),界面不把旧值当现状(设置页变只读)。</param>
public sealed record DeviceSettingsMirror(bool Known, long Rev, DeviceSettingsDto? Settings)
{
    public static readonly DeviceSettingsMirror Unknown = new(false, 0, null);
}

public sealed record SettingsMirror(ImmutableDictionary<string, DeviceSettingsMirror> Devices)
{
    public static readonly SettingsMirror Empty = new(ImmutableDictionary<string, DeviceSettingsMirror>.Empty);

    public DeviceSettingsMirror Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceSettingsMirror.Unknown;
}

/// <summary>
/// 一台设备上定时任务与运行的镜像(<c>schedules</c> 主题,全量权威)。<see cref="Runs"/> = 活动的运行 + 最近已结束的;更早的用 <c>ScheduleActions.ListRunsAsync</c> 按需翻页,不进 Store。
/// </summary>
public sealed record DeviceSchedules(bool Known, long Rev, ImmutableArray<ScheduledTaskDto> Tasks, ImmutableArray<ScheduledRunDto> Runs)
{
    public static readonly DeviceSchedules Unknown = new(false, 0, [], []);

    /// <summary>排队 / 运行中的运行(任务卡上的「运行中」状态点只由它回答:一个事实一个真源)。</summary>
    public IEnumerable<ScheduledRunDto> ActiveRuns => Runs.Where(static r => r.Status is "queued" or "running");

    public bool IsRunning(string taskId) => ActiveRuns.Any(r => r.TaskId == taskId);
}

public sealed record SchedulesMirror(ImmutableDictionary<string, DeviceSchedules> Devices)
{
    public static readonly SchedulesMirror Empty = new(ImmutableDictionary<string, DeviceSchedules>.Empty);

    public DeviceSchedules Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceSchedules.Unknown;
}

/// <summary>
/// 设置 / 定时任务镜像的折叠规则(纯函数)。全量状态,收到不更旧的就整体替换(同版本也收:代理密码这类不改版本的变化会再发一份同版本的全量);
/// 重放的旧事件不能把状态改回旧值(规则 6);设备断开标为未知(规则 7)。
/// </summary>
public static class ServicesReducer
{
    public static SettingsMirror ApplySettings(SettingsMirror mirror, string deviceId, DeviceSettingsState state)
    {
        var current = mirror.Of(deviceId);
        if (current.Known && state.Rev < current.Rev)
        {
            return mirror;
        }

        return mirror with { Devices = mirror.Devices.SetItem(deviceId, new DeviceSettingsMirror(true, state.Rev, state.Settings)) };
    }

    public static SettingsMirror MarkSettingsUnknown(SettingsMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;

    public static SchedulesMirror ApplySchedules(SchedulesMirror mirror, string deviceId, SchedulesTopicState state)
    {
        var current = mirror.Of(deviceId);
        if (current.Known && state.Rev < current.Rev)
        {
            return mirror;
        }

        return mirror with { Devices = mirror.Devices.SetItem(deviceId, new DeviceSchedules(true, state.Rev, [.. state.Tasks], [.. state.Runs])) };
    }

    public static SchedulesMirror MarkSchedulesUnknown(SchedulesMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;
}
