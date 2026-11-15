using Ncw.Client.Stores;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>设置 / 定时任务镜像的折叠规则(纯函数):全量状态、只收不更旧的、同版本也收(代理密码这类不改版本的变化)、断开是未知而不是判结束(AGENTS.md §3 规则 6、7)。</summary>
public sealed class ServicesMirrorTests
{
    private const string Device = "dev-1";

    private static DeviceSettingsDto Settings(int maxOutput = 32_000, bool hasPassword = false) => new(
        ModelPickDto.None, ModelPickDto.None, ModelPickDto.None, ModelPickDto.None, "inherit", ModelPickDto.None, ModelPickDto.None, ModelPickDto.None,
        "ask", "auto", true, maxOutput, true, true, false, true, "system", 30, 600, true, false, "",
        new SubagentSettingsDto(ModelPickDto.None, "inherit", 4, 4),
        new ProxySettingsDto(false, "system", "http", "", 0, false, "", "", hasPassword),
        new GatewaySettingsDto(false, false, 19836), new DataSettingsDto(null, "manual"), new PersonalizationSettingsDto("", "", ""));

    private static ScheduledRunDto Run(string id, string status) => new(id, "t1", "s", "scheduled", status, 1, null, null, null, null);

    private static ScheduledTaskDto Task(string id) => new(
        id, "n", "p", "ws", "m", null, new ScheduleRuleDto("daily", Time: "09:00"), "UTC", new RepeatWindowDto(false), true, 100, 1, 1, "user", 0);

    [Fact]
    public void A_newer_settings_state_replaces_the_mirror_and_an_older_one_is_ignored()
    {
        var mirror = ServicesReducer.ApplySettings(SettingsMirror.Empty, Device, new DeviceSettingsState(5, Settings(8000)));
        Assert.Equal((true, 5L, 8000), (mirror.Of(Device).Known, mirror.Of(Device).Rev, mirror.Of(Device).Settings!.MaxOutputTokens));

        var stale = ServicesReducer.ApplySettings(mirror, Device, new DeviceSettingsState(4, Settings(1024)));
        Assert.Same(mirror, stale); // 重放的旧事件改不回去

        var newer = ServicesReducer.ApplySettings(mirror, Device, new DeviceSettingsState(6, Settings(9000)));
        Assert.Equal(9000, newer.Of(Device).Settings!.MaxOutputTokens);
    }

    [Fact]
    public void A_same_revision_state_is_accepted_because_the_proxy_password_flag_changes_without_a_version_bump()
    {
        var mirror = ServicesReducer.ApplySettings(SettingsMirror.Empty, Device, new DeviceSettingsState(5, Settings()));
        var next = ServicesReducer.ApplySettings(mirror, Device, new DeviceSettingsState(5, Settings(hasPassword: true)));

        Assert.True(next.Of(Device).Settings!.Proxy.HasPassword);
    }

    [Fact]
    public void A_disconnect_marks_settings_unknown_and_the_next_snapshot_makes_them_known_again_even_with_a_lower_revision()
    {
        var mirror = ServicesReducer.ApplySettings(SettingsMirror.Empty, Device, new DeviceSettingsState(9, Settings()));
        var unknown = ServicesReducer.MarkSettingsUnknown(mirror, Device);
        Assert.False(unknown.Of(Device).Known);
        Assert.Equal(9, unknown.Of(Device).Rev); // 不丢版本:重连后的快照版本不更旧才收

        // 设备重启后版本号从头计:未知状态下收到的快照无条件替换(Known = false 时不比版本)
        var restarted = ServicesReducer.ApplySettings(unknown, Device, new DeviceSettingsState(1, Settings(4096)));
        Assert.Equal((true, 1L, 4096), (restarted.Of(Device).Known, restarted.Of(Device).Rev, restarted.Of(Device).Settings!.MaxOutputTokens));
        Assert.Equal(DeviceSettingsMirror.Unknown, SettingsMirror.Empty.Of("other"));
    }

    [Fact]
    public void Schedules_state_is_a_full_replacement_and_running_is_answered_only_by_the_active_runs_in_it()
    {
        var running = new SchedulesTopicState(3, [Task("t1")], [Run("r1", "running")]);
        var mirror = ServicesReducer.ApplySchedules(SchedulesMirror.Empty, Device, running);
        Assert.True(mirror.Of(Device).IsRunning("t1"));

        var done = ServicesReducer.ApplySchedules(mirror, Device, new SchedulesTopicState(4, [Task("t1")], [Run("r1", "success")]));
        Assert.False(done.Of(Device).IsRunning("t1"));

        // 终态压过运行中:重放的旧广播(版本 3)不能把状态改回「运行中」
        var replay = ServicesReducer.ApplySchedules(done, Device, running);
        Assert.Same(done, replay);
        Assert.False(replay.Of(Device).IsRunning("t1"));
    }

    [Fact]
    public void A_disconnect_keeps_the_last_known_schedules_but_marks_them_unknown_it_neither_ends_nor_keeps_counting_runs()
    {
        var mirror = ServicesReducer.ApplySchedules(SchedulesMirror.Empty, Device, new SchedulesTopicState(3, [Task("t1")], [Run("r1", "running")]));
        var unknown = ServicesReducer.MarkSchedulesUnknown(mirror, Device);

        Assert.False(unknown.Of(Device).Known); // 界面据此显示「状态未知」,不把旧的「运行中」当现状,也不判成结束
        Assert.Single(unknown.Of(Device).Tasks);
        Assert.Same(unknown, ServicesReducer.MarkSchedulesUnknown(unknown, Device));
        Assert.Same(SchedulesMirror.Empty, ServicesReducer.MarkSchedulesUnknown(SchedulesMirror.Empty, Device));
    }

    [Fact]
    public void Devices_are_mirrored_independently()
    {
        var mirror = ServicesReducer.ApplySettings(SettingsMirror.Empty, "a", new DeviceSettingsState(1, Settings(8000)));
        mirror = ServicesReducer.ApplySettings(mirror, "b", new DeviceSettingsState(1, Settings(9000)));

        Assert.Equal((8000, 9000), (mirror.Of("a").Settings!.MaxOutputTokens, mirror.Of("b").Settings!.MaxOutputTokens));
    }
}
